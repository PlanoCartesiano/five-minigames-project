using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using UnityEngine;
using UnityEditor;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.Reactor;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Manages syncing of assets. Some assets do not have paths; these assets are saved in one or more scenes. Assets
     * are synced when they are referenced if their type was registered as a syncable type with
     * sfLoader.Get().RegisterSyncableType<T>(). Scriptable objects without paths are also uploaded. The uploader
     * computes a checksum for assets with paths. Other users compute their own checksum from their values and the
     * server values. If their checksum does not match the one the asset was uploaded with or the one computed from the
     * server values, the asset will not sync for that user.
     */
    public class sfAssetTranslator : sfBaseUObjectTranslator
    {
        private HashSet<UObject> m_conflictingAssets = new HashSet<UObject>();
        private HashSet<UObject> m_lockedAssets = new HashSet<UObject>();

        // sfObjects without paths that we need to create UObjects for.
        private HashSet<sfObject> m_createSet = new HashSet<sfObject>();
        // sfObjects without paths that had references removed. They are deleted if they are no longer referenced and
        // we are subscribed to all scenes.
        private HashSet<sfObject> m_removedReferenceSet = new HashSet<sfObject>();

        /**
         * Initialization
         */
        public override void Initialize()
        {
            PostUObjectChange.Add<TerrainLayer>((UObject uobj) => sfUI.Get().MarkSceneViewStale());
            m_propertyChangeHandlers.Add<UObject>(sfProp.Path, HandlePathChange);
        }

        /**
         * Called after connecting to a sesson. Registers event handlers.
         */
        public override void OnSessionConnect()
        {
            sfLoader.Get().OnCacheAsset += HandleCacheAsset;
            SceneFusion.Get().OnUpdate += Update;
            SceneFusion.Get().Service.Session.OnRemoveReference += HandleRemoveReference;
        }

        /**
         * Called after disconnecting from a session. Unregisters event handlers, unlocks assets and clears data
         * structures.
         */
        public override void OnSessionDisconnect()
        {
            sfLoader.Get().OnCacheAsset -= HandleCacheAsset;
            SceneFusion.Get().OnUpdate -= Update;
            SceneFusion.Get().Service.Session.OnRemoveReference -= HandleRemoveReference;

            // Unlock all assets
            foreach (UObject asset in m_lockedAssets)
            {
                if (asset != null)
                {
                    asset.hideFlags &= ~HideFlags.NotEditable;
                    sfUI.Get().MarkInspectorStale(asset);
                }
            }
            m_lockedAssets.Clear();
            m_conflictingAssets.Clear();
            m_createSet.Clear();
            m_removedReferenceSet.Clear();
        }

        /**
         * Checks if a uobject can be synced. An object can be synced if:
         *  - It is not null.
         *  - It is not a built-in asset.
         *  - It is not a stand-in.
         *  - It is a syncable type (registered with sfLoader.Get().RegisterSyncableType) or it is a scriptable object
         *      without a path.
         */
        public bool IsSyncable(UObject uobj)
        {
            return uobj != null && !sfLoader.Get().IsBuiltInAsset(uobj) && !sfLoader.Get().IsStandIn(uobj) &&
                (sfLoader.Get().IsSyncableAssetType(uobj) || (uobj is ScriptableObject &&
                !sfLoader.Get().IsAsset(uobj)));
        }

        /**
         * Called every frame. Deletes unreferenced assets without paths. Creates pathless assets for sfObjects in the
         * create set.
         */
        private void Update(float deltaTime)
        {
            DeleteUnreferencedPathlessAssets();
            CreatePathlessAssets();
        }

        /**
         * Deletes sfObjects in the removed referenced set that have no references if we are subscribed to all scenes.
         */
        private void DeleteUnreferencedPathlessAssets()
        {
            if (m_removedReferenceSet.Count == 0)
            {
                return;
            }
            // If we are not subscribed to all scenes, don't delete anything as assets could be referenced from scenes
            // we aren't subscribed to.
            sfSceneTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfSceneTranslator>(
                sfType.Scene);
            if (translator != null && !translator.IsSubscribedToAllScenes())
            {
                return;
            }
            foreach (sfObject obj in m_removedReferenceSet)
            {
                if (SceneFusion.Get().Service.Session.GetReferences(obj).Length == 0)
                {
                    SceneFusion.Get().Service.Session.Delete(obj);
                }
            }
            m_removedReferenceSet.Clear();
        }

        /**
         * Creates pathless assets for sfObjects in the create set that do not already have uobjects in the
         * sfObjectMap.
         */
        private void CreatePathlessAssets()
        {
            if (m_createSet.Count == 0)
            {
                return;
            }
            // If the assets we create reference other pathless assets, they may get added to the create set, so we
            // create an array from the create set and iterate that to avoid the set being modified while iterating it.
            sfObject[] toCreate = m_createSet.ToArray();
            m_createSet.Clear();
            foreach (sfObject obj in toCreate)
            {
                if (!sfObjectMap.Get().Contains(obj))
                {
                    sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
                    string className = (string)properties[sfProp.Class];
                    Type type = sfTypeCache.Get().Load(className);
                    if (type == null)
                    {
                        ksLog.Warning(this, "Could not load type '" + className + "'. Cannot create pathless asset.");
                        continue;
                    }
                    UObject asset = sfLoader.Get().Create(type);
                    if (asset == null)
                    {
                        ksLog.Warning(this, "Could not create '" + className + "' pathless asset.");
                        continue;
                    }
                    InitializeAsset(obj, asset);
                }
            }
        }

        /**
         * Gets the UObject for an sfObject. Tries to get the UObject from the sfObjectMap, then if the sfObject has a
         * path property, tries to load it by asset path. Otherwise if current is a pathless asset whose class matches
         * the class property and isn't already mapped to an sfObject, maps current to the sfObject and returns it,
         * otherwise adds the sfObject to the create set to have an asset created for it in the next update if one
         * isn't mapped to it before then.
         * 
         * @param   sfObject obj to get UObject for.
         * @param   UObject current value of the serialized property we are getting the UObject reference for.
         * @return  UObject for the sfObject.
         */
        public override UObject GetUObject(sfObject obj, UObject current = null)
        {
            UObject uobj = base.GetUObject(obj);
            if (uobj == null)
            {
                sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
                if (properties.HasField(sfProp.Path))
                {
                    sfAssetInfo assetInfo = sfPropertyUtils.GetAssetInfo(properties);
                    sfBaseProperty prop;
                    string guid =  properties.TryGetField(sfProp.Guid, out prop) ? (string)prop : null;
                    uobj = sfLoader.Get().Load(assetInfo, guid);
                }
                else if (CanLink(current, (string)properties[sfProp.Class]))
                {
                    InitializeAsset(obj, current);
                    uobj = current;
                }
                else
                {
                    m_createSet.Add(obj);
                }
            }
            return uobj;
        }

        /**
         * Creates an sfObject for a uobject if the object is a syncable asset type.
         *
         * @param   UObject uobj to create sfObject for.
         * @param   sfObject outObj created for the uobject.
         * @return  bool true if the uobject was handled by this translator.
         */
        public override bool TryCreate(UObject uobj, out sfObject outObj)
        {
            if (!IsSyncable(uobj))
            {
                outObj = null;
                return false;
            }
            outObj = Create(uobj);
            return true;
        }

        /**
         * Creates an sfObject for an asset.
         *
         * @param   UObject asset to create sfObject for.
         * @return  sfObject object for the asset, or null if one could not be created.
         */
        public sfObject Create(UObject asset)
        {
            // Do not create an sfObject if the uobject already has an sfObject, or if it conflicts with the server.
            // version.
            if (sfObjectMap.Get().Contains(asset) || m_conflictingAssets.Contains(asset))
            {
                return null;
            }
            sfObject obj = CreateObject(asset, sfType.Asset);
            if (obj == null)
            {
                return null;
            }
            sfDictionaryProperty dict = (sfDictionaryProperty)obj.Property;
            sfAssetInfo info = sfLoader.Get().GetAssetInfo(asset);
            if (info.IsValid)
            {
                // The asset has a path.
                sfPropertyUtils.SetAssetInfoProperties(dict, info);
                dict[sfProp.CheckSum] = sfChecksum.Get().Fletcher64(asset);

                string guid;
                long fileId;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out fileId))
                {
                    dict[sfProp.Guid] = guid;
                }
            }
            else
            {
                // The asset has no path.
                dict[sfProp.Class] = asset.GetType().ToString();
            }

            if (Selection.Contains(asset))
            {
                obj.RequestLock();
            }
            SceneFusion.Get().Service.Session.Create(obj);
            return obj;
        }

        /**
         * Called when an object is created by another user. Loads the asset by path if it has a path. Otherwise,
         * attempts to find a pathless asset uobject to map to it from the references to the serialized properties. If
         * one cannot be found add the sfObject to the create set to have an asset created for it on the next update.
         * Applies the sfObject's properties to the asset.
         *
         * @param   sfObject obj that was created.
         * @param   int childIndex of new object. -1 if object is a root.
         */
        public override void OnCreate(sfObject obj, int childIndex)
        {
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            UObject asset;
            if (properties.HasField(sfProp.Path))
            {
                // The asset has a path.
                asset = LoadAsset(obj);
            }
            else
            {
                // The asset has no path.
                sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
                if (references.Length == 0)
                {
                    // Do not create the asset uobject until it is referenced.
                    return;
                }
                // Find a pathless asset for this sfObject from the references.
                if (FindPathlessAssetFromReferences(references, (string)properties[sfProp.Class], out asset) && 
                    asset == null)
                {
                    // We could not find a pathless asset for this sfObject and we found a uobject for at least one of
                    // the referencing objcts. Add it to the create set to have one created in the next update.
                    m_createSet.Add(obj);
                }
            }

            if (asset != null)
            {
                InitializeAsset(obj, asset);
            }
        }

        /**
         * Checks if a uobject can be linked to a pathless asset's sfObject with the given class name. To be linkable the
         * uobject must:
         *  - Not be null.
         *  - Not be mapped to an sfObject.
         *  - Not have a path.
         *  - Be the correct type. Derived classes of className do not count.
         *  
         *  @param  UObject uobj to check.
         *  @param  string className including namespace that the uobject must have.
         *  @return bool true if the uobject can be linked.
         */
        private bool CanLink(UObject uobj, string className)
        {
            return uobj != null && !sfObjectMap.Get().Contains(uobj) && !sfLoader.Get().IsAsset(uobj) &&
                uobj.GetType().ToString() == className;
        }

        /**
         * Maps an sfObject to an asset. Applies sfObject properties to the asset and sets references to the asset.
         * Locks the asset if the sfObject is locked.
         * 
         * @param   sfObject obj
         * @param   UObject asset.
         */
        private void InitializeAsset(sfObject obj, UObject asset)
        {
            sfObjectMap.Get().Add(obj, asset);
            sfPropertyManager.Get().ApplyProperties(asset, (sfDictionaryProperty)obj.Property);
            sfUI.Get().MarkProjectBrowserStale();

            // Set references to this asset.
            sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
            sfPropertyManager.Get().SetReferences(asset, references);

            if (obj.IsLocked)
            {
                OnLock(obj);
            }
        }

        /**
         * Loads the asset for an sfObject from its sfAssetInfo and checks if the checksum matches either the checksum
         * the asset was uploaded with, or the checksum computed from the current server values. If the checksum does
         * not match, returns null. Creates the asset if it does not exist and is a syncable type.
         * 
         * @param   sfObject obj to load asset for.
         */
        private UObject LoadAsset(sfObject obj)
        {
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            sfAssetInfo info = sfPropertyUtils.GetAssetInfo(properties);
            sfBaseProperty prop;
            string guid = properties.TryGetField(sfProp.Guid, out prop) ? (string)prop : null;
            UObject asset = sfLoader.Get().Load(info, guid);
            if (asset == null || sfLoader.Get().IsStandIn(asset))
            {
                ksLog.Warning(this, "Asset " + info + " cannot be generated. " +
                    "Did you miss calling sfLoader.Get().RegisterSyncableType<T>()? " +
                    "Make sure all users have the same version of the project that registers the same syncable " +
                    "asset types before starting a session to ensure these assets sync properly.");
                return null;
            }

            sfObject current = sfObjectMap.Get().GetSFObject(asset);
            if (current != null)
            {
                // The asset was created twice, which can happen if two users try to create the asset at the same time. Keep
                // the version that was created first and delete the second one.
                ksLog.Warning(this, "Asset " + info + " was uploaded by multiple users. The second version will " +
                    "be ignored.");
                if (current.IsCreated)
                {
                    ReplaceReferences(obj, current);
                    SceneFusion.Get().Service.Session.Delete(obj);
                    return null;
                }
                ReplaceReferences(current, obj);
                SceneFusion.Get().Service.Session.Delete(current);
                sfObjectMap.Get().Remove(current);
            }

            // If the asset was created on load, this user did not have the asset so we don't have to check for
            // conflicts.
            if (!sfLoader.Get().WasCreatedOnLoad(asset))
            {
                ulong checksum = sfChecksum.Get().Fletcher64(asset);
                ulong serverChecksum = (ulong)properties[sfProp.CheckSum];
                // If our checksum does not match the initial checksum the asset was uploaded with and our asset has
                // differet property values from the server, the asset is conflicting and we won't sync it.
                if (checksum != serverChecksum && 
                    !sfPropertyManager.Get().HasSameProperties(asset, properties))
                {
                    string message = "Your asset " + info + " conflicts with the server version and will not sync.";
                    sfNotification.Create(sfNotificationCategory.AssetConflict, message, asset);
                    m_conflictingAssets.Add(asset);

                    // Set references to the asset.
                    sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
                    sfPropertyManager.Get().SetReferences(asset, references);
                    return null;
                }
            }
            return asset;
        }

        /**
         * Finds a pathless asset with the given class name type that isn't already mapped to an sfObject from the
         * given references.
         * 
         * @param   sfReferenceProperty[] references
         * @param   string className including namespace to look for. Derived classes do not count.
         * @param   out UObject asset of the given type found from the references. Null if none was found.
         * @return  bool true if at least one of the referencing objects was mapped to a uobject.
         */
        private bool FindPathlessAssetFromReferences(
            sfReferenceProperty[] references,
            string className,
            out UObject asset)
        {
            bool foundReferencingUObj = false;
            asset = null;
            for (int i = 0; i < references.Length; i++)
            {
                sfReferenceProperty reference = references[i];
                UObject uobj = sfObjectMap.Get().GetUObject(reference.GetContainerObject());
                if (uobj == null)
                {
                    continue;
                }
                foundReferencingUObj = true;
                SerializedProperty sprop = sfPropertyManager.Get().GetSerializedProperty(
                    new SerializedObject(uobj), reference);
                if (sprop != null && CanLink(sprop.objectReferenceValue, className))
                {
                    asset = sprop.objectReferenceValue;
                    break;
                }
            }
            return foundReferencingUObj;
        }

        /**
         * Called when an asset object is deleted. Pathless assets are deleted when they are no longer referenced. 
         * Removes the object from the object map if there are no references to it, otherwise reuploads it, which can
         * happen if one user creates a reference to a pathless asset at the same time as another user deletes all
         * references to that asset. Assets with paths are never deleted.
         * 
         * @param   sfObject that was deleted.
         */
        public override void OnDelete(sfObject obj)
        {
            if (SceneFusion.Get().Service.Session.GetReferences(obj).Length == 0)
            {
                sfObjectMap.Get().Remove(obj);
            }
            else
            {
                SceneFusion.Get().Service.Session.Create(obj);
            }
        }

        /**
         * Called when a locally deleted asset is confirmed as deleted. Calls OnDelete to remove the object from the
         * sfObject map, or reupload it if it is still referenced. Assets with paths are never deleted.
         */
        public override void OnConfirmDelete(sfObject obj, bool unsubscribed)
        {
            OnDelete(obj);
        }

        /**
         * Called when a synced reference is removed. If the reference is to an asset without a path, adds the sfObject
         * to the removed reference set to be deleted in the next update if it is not referenced. We do not delete root
         * pathless assets if we are not subscribed to all scenes, as they could be referenced in scenes we are not
         * subscribed to.
         * 
         * @param   sfReferenceProperty reference
         */
        private void HandleRemoveReference(sfReferenceProperty reference, uint objectId)
        {
            sfObject obj = SceneFusion.Get().Service.Session.GetObject(objectId);
            if (obj != null && obj.Type == sfType.Asset && !((sfDictionaryProperty)obj.Property).HasField(sfProp.Path))
            {
                m_removedReferenceSet.Add(obj);
            }
        }

        /**
         * Called when an asset's path property changes. We don't currently support renaming assets, so this only
         * happens when a pathless asset is saved to an asset file. Saves the asset to an asset file at the given path,
         * or if an asset at that path already exists, deletes the pathless asset and replaces references to it with
         * the asset at that path.
         * 
         * @param   UObject uobj whose path changed.
         * @param   sfBaseProperty property - path property that changed.
         * @return  bool true to indicate the event was handled.
         */
        private bool HandlePathChange(UObject uobj, sfBaseProperty property)
        {
            if (property == null)
            {
                // If the property is null, that means we tried to change the path but the request was denied because
                // the object became locked when someone else changed its parent. Call HandleNewAsset again to change
                // the path again.
                sfAssetInfo info = sfLoader.Get().GetAssetInfo(uobj);
                if (info.IsValid)
                {
                    HandleCacheAsset(info, uobj);
                }
            }
            // If the loader says it is not an asset, that means it has no path.
            else if (!sfLoader.Get().IsAsset(uobj))
            {
                string path = (string)property;
                if (File.Exists(path))
                {
                    // An asset already exists at this path. Delete the pathless asset and replace it with the asset.
                    sfObjectMap.Get().Remove(uobj);
                    UObject.DestroyImmediate(uobj);
                    OnCreate(property.GetContainerObject(), -1);
                }
                else
                {
                    // Convert the pathless asset to an asset at the given path.
                    ksPathUtils.Create(path);
                    AssetDatabase.CreateAsset(uobj, path);
                    ksLog.Info(this, "Saved " + uobj.GetType() + " to'" + path + "'.");
                }
            }
            return true;
        }

        /**
         * Called when an asset is cached by sfLoader. If the asset was already synced as a pathless asset, is not a
         * sub asset, and is a syncable asset type, sets path, guid, and checksum properties on the sfObject to save it
         * at that path for other users.
         * 
         * @param   sfAssetInfo info for the cached asset.
         * @param   UObject asset that was cached.
         */
        private void HandleCacheAsset(sfAssetInfo info, UObject asset)
        {
            if (info.IsSubasset || !sfLoader.Get().IsSyncableAssetType(asset.GetType()))
            {
                return;
            }
            sfObject obj = sfObjectMap.Get().GetSFObject(asset);
            if (obj == null || obj.Type != sfType.Asset)
            {
                return;
            }
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            if (properties.HasField(sfProp.Path))
            {
                return;
            }
            properties[sfProp.CheckSum] = sfChecksum.Get().Fletcher64(asset);
            properties[sfProp.Path] = info.Path;

            string guid;
            long fileId;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out fileId))
            {
                properties[sfProp.Guid] = guid;
            }
        }

        /**
         * Called when a locally created asset object is confirmed as created. Refreshes the project browser to update
         * the icon for the asset.
         * 
         * @param   sfObject obj that whose creation was confirmed.
         */
        public override void OnConfirmCreate(sfObject obj)
        {
            sfUI.Get().MarkProjectBrowserStale();
        }

        /**
         * Called when an object is locked by another user.
         * 
         * @param   sfObject obj that was locked.
         */
        public override void OnLock(sfObject obj)
        {
            UObject asset = sfObjectMap.Get().GetUObject(obj);
            if (asset != null)
            {
                m_lockedAssets.Add(asset);
                asset.hideFlags |= HideFlags.NotEditable;
                sfUI.Get().MarkInspectorStale(asset);
                sfUI.Get().MarkProjectBrowserStale();
            }
        }

        /**
         * Called when an object is unlocked by another user.
         * 
         * @param   sfObject obj that was unlocked.
         */
        public override void OnUnlock(sfObject obj)
        {
            UObject asset = sfObjectMap.Get().GetUObject(obj);
            if (asset != null)
            {
                m_lockedAssets.Remove(asset);
                asset.hideFlags &= ~HideFlags.NotEditable;
                sfUI.Get().MarkInspectorStale(asset);
                sfUI.Get().MarkProjectBrowserStale();
            }
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
    }
}
