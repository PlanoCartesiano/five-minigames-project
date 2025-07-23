using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.Reactor;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Manages syncing of asset paths and handles notifications for missing assets. Each asset referenced that isn't
     * synced by another translator gets an asset path sfObject containing the asset path, guid, and for subassets, the
     * file id. The guid and file id are used for offline missing asset replacement. References to the asset are synced
     * as sfReferenceProperties referencing the asset path object. This allows us to find all synced references to an
     * asset, as well as track when new references are added or removed which we use for missing asset notifications.
     */
    public class sfAssetPathTranslator : sfBaseTranslator
    {
        private Dictionary<sfAssetInfo, sfObject> m_infoToObjectMap = new Dictionary<sfAssetInfo, sfObject>();
        // Keys are the sfObject id of the asset path object for the missing asset.
        private Dictionary<uint, sfNotification> m_notificationMap = new Dictionary<uint, sfNotification>();
        private List<KeyValuePair<sfObject, sfNotification>> m_notificationsToAdd =
            new List<KeyValuePair<sfObject, sfNotification>>();
        private HashSet<sfObject> m_missingObjects = new HashSet<sfObject>();
        private bool m_saved = false;

        /**
         * Called after connecting to a session. Registers event handlers.
         */
        public override void OnSessionConnect()
        {
            SceneFusion.Get().OnUpdate += Update;
            sfLoader.Get().OnLoadError += HandleLoadError;
            sfLoader.Get().OnFindMissingAsset += HandleFindMissingAsset;
            SceneFusion.Get().Service.Session.OnAddReference += HandleAddReference;
            SceneFusion.Get().Service.Session.OnRemoveReference += HandleRemoveReference;
            sfSceneSaveWatcher.Get().PreSave += PreSave;
            sfSceneSaveWatcher.Get().PostSave += PostSave;
        }

        /**
         * Called after disconnecting from a session. Unregisters event handlers.
         */
        public override void OnSessionDisconnect()
        {
            ConvertStandInsToGuids();
            SceneFusion.Get().OnUpdate -= Update;
            sfLoader.Get().OnLoadError -= HandleLoadError;
            sfLoader.Get().OnFindMissingAsset -= HandleFindMissingAsset;
            SceneFusion.Get().Service.Session.OnAddReference -= HandleAddReference;
            SceneFusion.Get().Service.Session.OnRemoveReference -= HandleRemoveReference;
            sfSceneSaveWatcher.Get().PreSave -= PreSave;
            sfSceneSaveWatcher.Get().PostSave -= PostSave;
            m_infoToObjectMap.Clear();
            m_notificationMap.Clear();
            m_missingObjects.Clear();
        }

        /**
         * Gets the UObject for an sfObject. Loads the UObject by asset path.
         * 
         * @param   sfObject obj to get UObject for.
         * @param   UObject current value of the serialized property we are getting the UObject reference for.
         * @return  UObject for the sfObject.
         */
        public override UObject GetUObject(sfObject obj, UObject current = null)
        {
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            sfAssetInfo assetInfo = sfPropertyUtils.GetAssetInfo(properties);
            return sfLoader.Get().Load(assetInfo);
        }

        /**
         * Called when an object is created by another user. Adds the object to the path to object map and sets
         * references to the asset the object represents.
         *
         * @param   sfObject obj that was created.
         * @param   int childIndex of new object. -1 if object is a root.
         */
        public override void OnCreate(sfObject obj, int childIndex)
        {
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            sfAssetInfo info = sfPropertyUtils.GetAssetInfo(properties);
            sfObject current;
            if (m_infoToObjectMap.TryGetValue(info, out current))
            {
                // The asset path was created twice, which can happen if two users try to create the asset path at the
                // same time. Keep the version that was created first and delete the second one.
                if (current.IsCreated)
                {
                    ReplaceReferences(obj, current);
                    SceneFusion.Get().Service.Session.Delete(obj);
                    return;
                }
                ReplaceReferences(current, obj);
                SceneFusion.Get().Service.Session.Delete(current);
            }
            m_infoToObjectMap[info] = obj;

            // Set references to this asset.
            sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
            if (references.Length > 0)
            {
                UObject asset = sfLoader.Get().Load(info);
                if (asset != null)
                {
                    sfPropertyManager.Get().SetReferences(asset, references);
                }
            }
        }

        /**
         * Gets the path object for an asset info.
         * 
         * @param   afAssetInfo info to get path object for.
         * @return  sfObject path object.
         */
        public sfObject GetPathObject(sfAssetInfo info)
        {
            sfObject obj;
            m_infoToObjectMap.TryGetValue(info, out obj);
            return obj;
        }

        /**
         * Gets the path object for an asset info. Creates one if it does not already exist.
         * 
         * @param   sfAssetInfo info to get path object for.
         * @param   UObject asset for the path object. Used to get the guid and file id if the path object needs to be
         *          created.
         * @return  sfObject path object.
         */
        public sfObject GetOrCreatePathObject(sfAssetInfo info, UObject asset)
        {
            if (!info.IsValid)
            {
                return null;
            }
            sfObject obj;
            if (!m_infoToObjectMap.TryGetValue(info, out obj) )
            {
                sfDictionaryProperty properties = new sfDictionaryProperty();
                sfPropertyUtils.SetAssetInfoProperties(properties, info);
                string guid;
                long fileId;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out fileId))
                {
                    properties[sfProp.Guid] = guid;
                    // We only sync file id for non-library subassets and prefabs.
                    if (sfLoader.Get().IsLibraryAsset(asset))
                    {
                        properties[sfProp.IsLibraryAsset] = true;
                        if (asset is GameObject)
                        {
                            properties[sfProp.FileId] = fileId;
                        }
                    }
                    else if (AssetDatabase.IsSubAsset(asset))
                    {
                        properties[sfProp.FileId] = fileId;
                    }
                }

                obj = new sfObject(sfType.AssetPath, properties);
                SceneFusion.Get().Service.Session.Create(obj);
                m_infoToObjectMap[info] = obj;
            }
            return obj;
        }

        /**
         * Called every frame. Adds queued notifications to uobjects.
         * 
         * @param   float deltaTime in seconds since the last update.
         */
        private void Update(float deltaTime)
        {
            // Add queued notifications to uobjects.
            foreach (KeyValuePair<sfObject, sfNotification> pair in m_notificationsToAdd)
            {
                UObject uobj = sfObjectMap.Get().GetUObject(pair.Key);
                if (uobj != null)
                {
                    pair.Value.AddToObject(uobj);
                }
            }
            m_notificationsToAdd.Clear();
        }

        /**
         * Replaces all sfReferenceProperties referencing oldObj with newObj.
         * 
         * @param   sfObject oldObj
         * @param   sfObject newObj
         */
        private void ReplaceReferences(sfObject oldObj, sfObject newObj)
        {
            foreach (sfReferenceProperty reference in SceneFusion.Get().Service.Session.GetReferences(oldObj))
            {
                reference.ObjectId = newObj.Id;
            }
        }

        /**
         * Called when the sfLoader fails to load an asset. Creates a missing asset notification.
         * 
         * @param   sfAssetInfo info for asset that failed to load.
         * @param   string message - error message.
         */
        private void HandleLoadError(sfAssetInfo info, string message)
        {
            sfObject obj = GetPathObject(info);
            if (obj != null && m_missingObjects.Add(obj))
            {
                sfNotification notification = sfNotification.Create(sfNotificationCategory.MissingAsset, message);
                m_notificationMap[obj.Id] = notification;
                sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
                foreach (sfReferenceProperty reference in references)
                {
                    // Add the notification on the next update in case the sfObject isn't linked to a UObject yet.
                    m_notificationsToAdd.Add(new KeyValuePair<sfObject, sfNotification>(
                        reference.GetContainerObject(), notification));
                }
            }
        }

        /**
         * Called when a missing asset is found. Clears the missing asset notification and updates references to the
         * asset.
         * 
         * @param   sfAssetInfo info for the asset that was previously missing.
         * @param   UObject asset that was previously missing.
         */
        private void HandleFindMissingAsset(sfAssetInfo info, UObject asset)
        {
            sfObject obj = GetPathObject(info);
            if (obj != null)
            {
                m_missingObjects.Remove(obj);
                sfNotification notification;
                if (m_notificationMap.Remove(obj.Id, out notification))
                {
                    notification.Clear();
                }

                sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
                if (references.Length > 0)
                {
                    sfPropertyManager.Get().SetReferences(asset, references);
                    ksLog.Info(this, "Replaced " + references.Length + " stand-in reference(s) for " + info + ".");
                }
            }
        }

        /**
         * Called when a reference is to an object is synced. If the reference is for a missing asset, adds a
         * notification to the uobject with the reference.
         * 
         * @param   sfReferenceProperty reference.
         */
        private void HandleAddReference(sfReferenceProperty reference)
        {
            sfNotification notification;
            if (m_notificationMap.TryGetValue(reference.ObjectId, out notification))
            {
                // Add the notification on the next update in case the sfObject isn't linked to a UObject yet.
                m_notificationsToAdd.Add(new KeyValuePair<sfObject, sfNotification>(
                    reference.GetContainerObject(), notification));
            }
        }

        /**
         * Called when a synced reference is removed. If the reference was for a missing asset, removes the
         * notification from the uobject with the reference.
         * 
         * @param   sfReferenceProperty reference
         * @param   uint objectId of the object that was referenced.
         */
        private void HandleRemoveReference(sfReferenceProperty reference, uint objectId)
        {
            sfNotification notification;
            if (m_notificationMap.TryGetValue(objectId, out notification))
            {
                UObject uobj = sfObjectMap.Get().GetUObject(reference.GetContainerObject());
                if (uobj != null)
                {
                    notification.RemoveFromObject(uobj);
                }
            }
        }

        /**
         * Creates assets for missing asset stand-ins with the guid and file id of the missing assets and updates
         * references to reference the new assets, then deletes the new assets so stand-in references become missing
         * object references with the correct guid and file id that Unity can automatically update when the asset with
         * that guid and file id becomes available.
         */
        private void ConvertStandInsToGuids()
        {
            foreach (sfObject obj in m_missingObjects)
            {
                sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
                sfAssetInfo info = sfPropertyUtils.GetAssetInfo(properties);
                UObject standIn = sfLoader.Get().Load(info);
                if (standIn == null)
                {
                    continue;
                }
                string guid = (string)properties[sfProp.Guid];
                sfBaseProperty prop;
                bool isLibraryAsset = false;
                if (properties.TryGetField(sfProp.IsLibraryAsset, out prop))
                {
                    isLibraryAsset = (bool)prop;
                }
                long fileId = 0;
                // We only sync file id for non-library subassets and prefabs.
                if (properties.TryGetField(sfProp.FileId, out prop))
                {
                    fileId = (long)prop;
                }
                UObject asset = sfLoader.Get().CreateStandInAssetWithGuid(standIn, guid, isLibraryAsset, fileId);
                if (asset == null)
                {
                    ksLog.Warning(this, "Unable to create stand-in " + (isLibraryAsset ? "library " : "") +
                        "asset for " + info + ". Offline missing asset replacement will not work for this asset.");
                }
                sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
                sfPropertyManager.Get().SetReferences(asset, references);
            }
            // Delete the temporary folder containing the new assets.
            ksPathUtils.Delete(sfPaths.Temp, true);
            AssetDatabase.Refresh();
        }

        /**
         * Called before saving a scene. Converts stand-ins to missing objects with the correct guid and file id.
         * 
         * @param   Scene scene being saved.
         */
        private void PreSave(Scene scene)
        {
            if (!m_saved)
            {
                // Set saved flag to avoid saving multiple times if we are saving multiple scenes at once.
                m_saved = true;
                ConvertStandInsToGuids();
            }
        }

        /**
         * Called after scenes are saved. Sets references that were changed to missing objects guid references before
         * saving back to stand-in references.
         */
        private void PostSave()
        {
            m_saved = false;
            foreach (sfObject obj in m_missingObjects)
            {
                sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
                sfAssetInfo info = sfPropertyUtils.GetAssetInfo(properties);
                UObject standIn = sfLoader.Get().Load(info);
                if (standIn == null)
                {
                    continue;
                }
                sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
                sfPropertyManager.Get().SetReferences(standIn, references, false);
            }
        }
    }
}
