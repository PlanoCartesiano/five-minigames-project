using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.Reactor;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Manages syncing of game objects.
     */
    public class sfGameObjectTranslator : sfBaseUObjectTranslator
    {
        /**
         * Lock type
         */
        public enum LockType
        {
            NOT_SYNCED,
            UNLOCKED,
            PARTIALLY_LOCKED,
            FULLY_LOCKED
        }

        /**
         * Lock state change event handler.
         * 
         * @param   GameObject gameObject whose lock state changed.
         * @param   LockType lockType
         * @param   sfUser user who owns the lock, or null if the object is not fully locked.
         */
        public delegate void OnLockStateChangeHandler(GameObject gameObject, LockType lockType, sfUser user);

        /**
         * Invoked when a game object's lock state changes.
         */
        public event OnLockStateChangeHandler OnLockStateChange;

        /**
         * Missing prefab asset event handler.
         * 
         * @param   UObject uobj refab instance that is missing its prefab asset.
         */
        public delegate void MissingPrefabHandler(UObject uobj);

        /**
         * Invoked when a prefab instance with a missing prefab asset is found. This is not the same as a prefab
         * stand-in (a game object with a sfMissingPrefab component).
         */
        public event MissingPrefabHandler OnMissingPrefab;

        // Don't sync gameobjects with these types of components
        private HashSet<Type> m_blacklist = new HashSet<Type>();

        /**
         * Stores the sfObject a game object has as its parent and its child index. Used to reattach the game object as
         * a child of the parent. This is used with unsynced game objects that do not have an sfObject containing the
         * parent relationship.
         */
        private struct AttachmentInfo
        {
            /**
             * Parents sfObject. This is a transform Component sfObject, or a Hierarchy sfObject for root game objects.
             */
            public sfObject Parent;

            /**
             * The child game object.
             */
            public GameObject Child;

            /**
             * The child index.
             */
            public int Index;

            /**
             * Constructor
             *
             * @param   GameObject child to create attachment info for. Throws an ArgumentException if this is a root
             *          object.
             */
            public AttachmentInfo(GameObject child)
            {
                if (child.transform.parent == null)
                {
                    throw new ArgumentException("Child cannot be a root game object.");
                }
                Parent = sfObjectMap.Get().GetSFObject(child.transform.parent);
                Child = child;
                Index = child.transform.GetSiblingIndex();
            }

            /**
             * Reattaches the child to its parent. If the parent doesn't exist, destroys the child.
             */
            public void Restore()
            {
                if (Child == null)
                {
                    return;
                }
                Transform parent = sfObjectMap.Get().Get<Transform>(Parent);
                if (parent == null)
                {
                    UObject.DestroyImmediate(Child);
                    return;
                }
                // We need to apply serialized properties before modifying the parent's children, otherwise the child
                // modifications may be lost when we apply serialized properties, corrupting the hierarchy.
                sfPropertyManager.Get().ApplySerializedProperties(parent);
                Child.transform.SetParent(parent);
                if (Index < parent.childCount - 1)
                {
                    Child.transform.SetSiblingIndex(Index);
                }
            }
        }

        private bool m_reachedObjectLimit = false;
        private bool m_relockObjects = false;
        private List<sfObject> m_recreateList = new List<sfObject>();
        private List<AttachmentInfo> m_reattachList = new List<AttachmentInfo>();
        
        private HashSet<GameObject> m_tempUnlockedObjects = new HashSet<GameObject>();
        private HashSet<sfObject> m_parentsWithNewChildren = new HashSet<sfObject>();
        private HashSet<sfObject> m_serverHierarchyChangedSet = new HashSet<sfObject>();
        private HashSet<sfObject> m_localHierarchyChangedSet = new HashSet<sfObject>();
        private HashSet<GameObject> m_applyPropertiesSet = new HashSet<GameObject>();
        private Dictionary<int, sfObject> m_instanceIdToSFObjectMap = new Dictionary<int, sfObject>();
        // Maps missing prefab paths to notifications.
        private Dictionary<string, sfNotification> m_missingPrefabNotificationMap = 
            new Dictionary<string, sfNotification>();

        /**
         * Initialization
         */
        public override void Initialize()
        {
            sfSessionsMenu.CanSync = IsSyncable;

            ksEditorEvents.OnImportAssets += HandleImportAssets;

            sfPropertyManager.Get().SyncedHiddenProperties.Add<GameObject>("m_IsActive");

            DontSyncObjectsWith<sfGuidList>();
            DontSyncObjectsWith<sfIgnore>();

            PostPropertyChange.Add<GameObject>("m_Name",
                (UObject uobj, sfBaseProperty prop) => sfHierarchyWatcher.Get().MarkHierarchyStale());
            PostPropertyChange.Add<GameObject>("m_Icon",
                (UObject uobj, sfBaseProperty prop) => sfLockManager.Get().RefreshLock((GameObject)uobj));
            PostPropertyChange.Add<GameObject>("m_IsActive",
                 (UObject uobj, sfBaseProperty prop) => sfUI.Get().MarkSceneViewStale());

            m_propertyChangeHandlers.Add<GameObject>(sfProp.Path, (UObject uobj, sfBaseProperty prop) =>
            {
                OnPrefabPathChange((GameObject)uobj, prop);
                return true;
            });
            m_propertyChangeHandlers.Add<GameObject>(sfProp.Index, (UObject uobj, sfBaseProperty prop) =>
            {
                return true;
            });
        }

        /**
         * Called after connecting to a session.
         */
        public override void OnSessionConnect()
        {
            SceneFusion.Get().PreUpdate += PreUpdate;
            SceneFusion.Get().OnUpdate += Update;
            sfSelectionWatcher.Get().OnSelect += OnSelect;
            sfSceneSaveWatcher.Get().PreSave += PreSave;
            sfSceneSaveWatcher.Get().PostSave += RelockObjects;
            sfUnityEventDispatcher.Get().OnCreate += OnCreateGameObject;
            sfUnityEventDispatcher.Get().OnDelete += OnDeleteGameObject;
            sfUnityEventDispatcher.Get().OnAddOrRemoveComponents += OnAddOrRemoveComponents;
            sfUnityEventDispatcher.Get().OnHierarchyStructureChange += OnHierarchyStructureChange;
            sfUnityEventDispatcher.Get().OnParentChange += MarkParentHierarchyStale;
            sfUnityEventDispatcher.Get().OnReorderChldren += MarkHierarchyStale;
            sfHierarchyWatcher.Get().OnDragCancel += RelockObjects;
            sfHierarchyWatcher.Get().OnDragComplete += OnHierarchyDragComplete;
            sfHierarchyWatcher.Get().OnValidateDrag += ValidateHierarchyDrag;
        }

        /**
         * Called after disconnecting from a session.
         */
        public override void OnSessionDisconnect()
        {
            m_reachedObjectLimit = false;
            m_recreateList.Clear();
            m_reattachList.Clear();
            m_tempUnlockedObjects.Clear();
            m_parentsWithNewChildren.Clear();
            m_serverHierarchyChangedSet.Clear();
            m_localHierarchyChangedSet.Clear();
            m_applyPropertiesSet.Clear();
            m_instanceIdToSFObjectMap.Clear();
            m_missingPrefabNotificationMap.Clear();

            SceneFusion.Get().PreUpdate -= PreUpdate;
            SceneFusion.Get().OnUpdate -= Update;
            sfSelectionWatcher.Get().OnSelect -= OnSelect;
            sfSceneSaveWatcher.Get().PreSave -= PreSave;
            sfSceneSaveWatcher.Get().PostSave -= RelockObjects;
            sfUnityEventDispatcher.Get().OnCreate -= OnCreateGameObject;
            sfUnityEventDispatcher.Get().OnDelete -= OnDeleteGameObject;
            sfUnityEventDispatcher.Get().OnAddOrRemoveComponents -= OnAddOrRemoveComponents;
            sfUnityEventDispatcher.Get().OnHierarchyStructureChange -= OnHierarchyStructureChange;
            sfUnityEventDispatcher.Get().OnParentChange -= MarkParentHierarchyStale;
            sfUnityEventDispatcher.Get().OnReorderChldren -= MarkHierarchyStale;
            sfHierarchyWatcher.Get().OnDragCancel -= RelockObjects;
            sfHierarchyWatcher.Get().OnDragComplete -= OnHierarchyDragComplete;
            sfHierarchyWatcher.Get().OnValidateDrag -= ValidateHierarchyDrag;

            // Unlock all game objects
            foreach (GameObject gameObject in sfUnityUtils.IterateGameObjects())
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
                if (obj != null && obj.IsLocked)
                {
                    Unlock(gameObject);
                }
            }
        }

        /**
         * Called every pre-update.
         * 
         * @param   float deltaTime in seconds since the last update.
         */
        private void PreUpdate(float deltaTime)
        {
            // Relock objects that were temporarily unlocked to make dragging in the hieararchy window work
            if (m_relockObjects)
            {
                RelockObjects();
                m_relockObjects = false;
            }

            RecreateGameObjects();

            // Sync the hierarchy for objects with local hierarchy changes
            SyncChangedHierarchies();

            // Reapply properties to game objects in the apply properties set and their components
            foreach (GameObject gameObject in m_applyPropertiesSet)
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
                if (obj == null || gameObject == null)
                {
                    continue;
                }
                sfPropertyManager.Get().ApplyProperties(gameObject, (sfDictionaryProperty)obj.Property);
                foreach (Component component in gameObject.GetComponents<Component>())
                {
                    obj = sfObjectMap.Get().GetSFObject(component);
                    if (obj != null)
                    {
                        sfPropertyManager.Get().ApplyProperties(component, (sfDictionaryProperty)obj.Property);
                    }
                }
            }
            m_applyPropertiesSet.Clear();

            // Upload new game objects
            UploadGameObjects();
        }

        /**
         * Called every update.
         * 
         * @param   float deltaTime in seconds since the last update.
         */
        private void Update(float deltaTime)
        {
            // Apply hierarchy changes from the server
            ApplyHierarchyChanges();

            if (!m_reachedObjectLimit)
            {
                sfSession session = SceneFusion.Get().Service.Session;
                if (session != null)
                {
                    uint limit = session.GetObjectLimit(sfType.GameObject);
                    if (limit != uint.MaxValue && session.GetObjectCount(sfType.GameObject) >= limit)
                    {
                        m_reachedObjectLimit = true;
                        EditorUtility.DisplayDialog("Game Object Limit Reached",
                            "You cannot create more game objects because you reached the " + limit +
                            " game object limit.", "OK");
                    }
                }
            }
        }

        /**
         * Prevents game objects with components of type T from syncing.
         */
        public void DontSyncObjectsWith<T>() where T : Component
        {
            m_blacklist.Add(typeof(T));
        }

        /**
         * Prevents game objects with components of the given type from syncing.
         * 
         * @param   Type type of component whose game objects should not sync.
         */
        public void DontSyncObjectsWith(Type type)
        {
            m_blacklist.Add(type);
        }

        /**
         * Checks if objects with the given component can be synced. Returns false if DontSyncObjectsWith was called
         * with the component's type or one of its base types.
         * 
         * @param   Component component to check.
         * @return  bool true if objects with the given component can be synced.
         */
        public bool CanSyncObjectsWith(Component component)
        {
            if (component == null)
            {
                return true;
            }
            foreach (Type type in m_blacklist)
            {
                if (type.IsAssignableFrom(component.GetType()))
                {
                    return false;
                }
            }
            return true;
        }

        /**
         * Checks if a game object can be synced. Objects can be synced if the following conditions are met:
         *  - They are not hidden
         *  - They can be saved in the editor
         *  - They have no components that prevent the object from syncing. sfIngore, sfGuidList, or a component type
         *      that DontSyncObjectsWith was called with will prevent objects from syncing.
         * 
         * @param   GameObject gameObject
         * @return  bool true if the game object can be synced.
         */
        public bool IsSyncable(GameObject gameObject)
        {
            return (gameObject.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor)) == HideFlags.None
                && !HasComponentThatPreventsSync(gameObject);
        }

        /**
         * Adds a mapping between an sfObject and a game object to the sfObjectMap and the instance id map.
         * 
         * @param   sfObject obj
         * @param   GameObject gameObject
         */
        private void AddMapping(sfObject obj, GameObject gameObject)
        {
            sfObjectMap.Get().Add(obj, gameObject);
            m_instanceIdToSFObjectMap[gameObject.GetInstanceID()] = obj;
        }

        /**
         * Removes a mapping between an sfObject and a game object from the sfObjectMap and the instance id map.
         * 
         * @param   GameObject gameObject to remove the mapping for.
         */
        private sfObject RemoveMapping(GameObject gameObject)
        {
            if ((object)gameObject == null)
            {
                return null;
            }
            sfObject obj = sfObjectMap.Get().Remove(gameObject);
            m_instanceIdToSFObjectMap.Remove(gameObject.GetInstanceID());
            return obj;
        }

        /**
         * Removes a mapping between an sfObject and a game object from the sfObjectMap and the instance id map.
         * 
         * @param   sfObject obj to remove the mapping for.
         */
        private GameObject RemoveMapping(sfObject obj)
        {
            GameObject gameObject = sfObjectMap.Get().Remove(obj) as GameObject;
            if ((object)gameObject != null)
            {
                m_instanceIdToSFObjectMap.Remove(gameObject.GetInstanceID());
            }
            return gameObject;
        }

        /**
         * Iterates a game object and its descendants and creates deterministic guids for any game objects that do not
         * have a guid.
         * 
         * @param   GameObject gameObject
         */
        public void CreateGuids(GameObject gameObject)
        {
            if (!IsSyncable(gameObject))
            {
                return;
            }
            sfGuidManager.Get().GetGuid(gameObject, true);
            foreach (Transform child in gameObject.transform)
            {
                CreateGuids(child.gameObject);
            }
        }

        /**
         * Applies server hierarchy changes to the local hierarchy (new children and child order) for objects with
         * server hierarchy changes.
         */
        public void ApplyHierarchyChanges()
        {
            foreach (sfObject parent in m_serverHierarchyChangedSet)
            {
                ApplyHierarchyChanges(parent);
            }
            m_serverHierarchyChangedSet.Clear();
        }

        /**
         * Applies server hierarchy changes to the local hierarchy (new children and child order) of an object.
         * 
         * @param   sfObject parent to apply hierarchy changes to. This should be a scene or transform object.
         */
        public void ApplyHierarchyChanges(sfObject parent)
        {
            // S: server order, L: local order
            int indexS = -1;
            int indexL = 0;
            int childIndex = 0;
            List<GameObject> childrenL = GetChildGameObjects(parent);
            if (childrenL == null)
            {
                return;
            }
            Transform parentTransform = sfObjectMap.Get().Get<Transform>(parent);// null if the parent is a scene
            Scene scene = parentTransform == null ? sfObjectEventDispatcher.Get()
                .GetTranslator<sfSceneTranslator>(sfType.Scene).GetScene(parent) : parentTransform.gameObject.scene;
            // Apply serialized properties now so child order isn't lost when properties are applied later.
            sfPropertyManager.Get().ApplySerializedProperties(parentTransform);
            Dictionary<sfObject, int> childIndexes = null;
            HashSet<GameObject> skipped = null;
            // Unity only allows you to set the child index of one child at a time. Each time you set the child index
            // is O(n). A naive algorithm could easily become O(n^2) if it sets the child index on every child. This
            // algorithm minimizes the amount of child indexes changes for better performance in most cases, though the
            // worst case is still O(n^2).
            foreach (sfObject objS in parent.Children)
            {
                if (objS.Type != sfType.GameObject)
                {
                    continue;
                }
                indexS++;
                GameObject gameObjectS = sfObjectMap.Get().Get<GameObject>(objS);
                if (gameObjectS == null)
                {
                    continue;
                }
                if (gameObjectS.transform.parent != parentTransform ||
                    (parentTransform == null && gameObjectS.scene != scene))
                {
                    // The game object has a different parent. Set the parent and child index.
                    sfComponentUtils.SetParent(gameObjectS.transform, parentTransform);
                    if (parentTransform == null && gameObjectS.scene != scene)
                    {
                        SceneManager.MoveGameObjectToScene(gameObjectS, scene);
                    }
                    gameObjectS.transform.SetSiblingIndex(childIndex);
                    sfObject transformObj = sfObjectMap.Get().GetSFObject(gameObjectS.transform);
                    if (transformObj != null)
                    {
                        sfPropertyManager.Get().ApplyProperties(gameObjectS.transform,
                            (sfDictionaryProperty)transformObj.Property);
                    }
                    sfHierarchyWatcher.Get().MarkHierarchyStale();
                    childIndex++;
                    continue;
                }

                if (skipped != null && skipped.Remove(gameObjectS))
                {
                    // We encountered this game object in the client list already and determined it should be moved
                    // when we found it in the server list. Set to chiledIndex -1 because its current index is lower
                    // and when we remove it, the destination index is decremented.
                    gameObjectS.transform.SetSiblingIndex(childIndex - 1);
                    sfHierarchyWatcher.Get().MarkHierarchyStale();
                    continue;
                }

                // DeltaS is how far to the left gameObjectS needs to move to get to the correct index. -1 means it
                // needs to be calculated. DeltaL is the difference between indexS and the server child index of
                // gameObjectL. We move the object with the greater delta as this gets us closer to the server state
                // and minimizes moves.
                int deltaS = -1;
                while (indexL < childrenL.Count)
                {
                    GameObject gameObjectL = childrenL[indexL];
                    if (gameObjectL == gameObjectS)
                    {
                        // The game object does not need to be moved.
                        indexL++;
                        childIndex++;
                        break;
                    }

                    sfObject objL = sfObjectMap.Get().GetSFObject(gameObjectL);
                    if (objL == null || objL.Parent != parent)
                    {
                        // The game object is not synced or has a different parent on the server. Its parent will
                        // change when we apply hierarchy changes to its new parent.
                        if (objL != null && !m_serverHierarchyChangedSet.Contains(objL.Parent))
                        {
                            ApplyHierarchyChanges(objL.Parent);
                        }
                        indexL++;
                        childIndex++;
                        continue;
                    }

                    if (childIndexes == null)
                    {
                        // Create map of sfObjects to child indexes for fast index lookups.
                        childIndexes = new Dictionary<sfObject, int>();
                        foreach (sfObject child in parent.Children)
                        {
                            childIndexes.Add(child, childIndexes.Count);
                        }
                    }

                    int deltaL = childIndexes[objL] - indexS;
                    if (deltaS < 0)
                    {
                        // Calculate deltaS
                        for (int i = indexL + 1; i < childrenL.Count; i++)
                        {
                            if (childrenL[i] == gameObjectS)
                            {
                                deltaS = i - indexL;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // We moved childIndex one to the right, so deltaS decreases by 1.
                        deltaS--;
                    }
                    if (deltaS > deltaL)
                    {
                        // Moving gameObjectS gets us closer to the server state than moving gameObjectL. Move
                        // gameObjectS.
                        gameObjectS.transform.SetSiblingIndex(childIndex);
                        childIndex++;
                        // Since gameObjectS was moved we need to remove it from the client child list so we don't
                        // encounter it where it no longer is.
                        childrenL.RemoveAt(indexL + deltaS);
                        sfHierarchyWatcher.Get().MarkHierarchyStale();
                        break;
                    }
                    else
                    {
                        // Moving gameObjectL gets us closer to the server state than moving gameObjectS. Add
                        // gameObjectL to the skipped set and move it once we encounter it in the server list.
                        if (skipped == null)
                        {
                            skipped = new HashSet<GameObject>();
                        }
                        skipped.Add(gameObjectL);
                        indexL++;
                        childIndex++;
                    }
                }
            }
            while (indexL < childrenL.Count)
            {
                GameObject gameObjectL = childrenL[indexL];
                sfObject objL = sfObjectMap.Get().GetSFObject(gameObjectL);
                if (objL != null && objL.IsSyncing && objL.Parent != parent && 
                    !m_serverHierarchyChangedSet.Contains(objL.Parent))
                {
                    // The game object has a different parent on the server. Apply hierarchy changes to its parent.
                    ApplyHierarchyChanges(objL.Parent);
                }
                indexL++;
            }
        }

        /**
         * If the game object is synced, adds its sfObject to the local hierarchy changed set to have its child order
         * synced in the next pre update.
         * 
         * @param   GameObject gameObject
         */
        public void MarkHierarchyStale(GameObject gameObject)
        {
            if (gameObject != null)
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(gameObject.transform);
                SyncHierarchyNextUpdate(obj);
            }
        }

        /**
         * If the game object is synced, adds the game object's parent sfObject to the set of objects with local
         * hierarchy changes to be synced in the next PreUpdate. If the sfObject is not synced but is syncable, adds
         * the parent to the upload set to have new children uploaded in the next PreUpdate. If the game object is
         * synced but the new parent is not, deletes the game object's sfObject.
         * 
         * @param   GameObject gameObject
         */
        public void MarkParentHierarchyStale(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                AddParentToUploadSet(gameObject);
                return;
            }
            sfObject parent;
            if (gameObject.transform.parent == null)
            {
                sfSceneTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfSceneTranslator>(
                    sfType.Hierarchy);
                parent = translator.GetHierarchyObject(gameObject.scene);
            }
            else
            {
                parent = sfObjectMap.Get().GetSFObject(gameObject.transform.parent);
            }
            if (parent != null && parent.IsSyncing)
            {
                m_localHierarchyChangedSet.Add(parent);
            }
            else
            {
                SyncDeletedObject(obj);
            }
        }

        /**
         * Sends hierarchy changes (new children and child order) for objects with local hierarchy changes. If the
         * children are locked, reverts them to their server location.
         */
        public void SyncChangedHierarchies()
        {
            foreach (sfObject parent in m_localHierarchyChangedSet)
            {
                SyncHierarchy(parent);
            }
            m_localHierarchyChangedSet.Clear();
        }

        /**
         * Sends hierarchy changes (new children and child order) for an object to the server on the next update. If
         * the children are locked, reverts them to their server location.
         */
        public void SyncHierarchyNextUpdate(sfObject parent)
        {
            if (parent != null)
            {
                m_localHierarchyChangedSet.Add(parent);
            }
        }

        /**
         * Sends hierarchy changes (new children and child order) for an object to the server. If the children are
         * locked, reverts them to their server location.
         */
        public void SyncHierarchy(sfObject parent)
        {
            if (!parent.IsSyncing)
            {
                return;
            }
            if (parent.IsFullyLocked)
            {
                // Put the parent in the server changed set so it is reverted to the server state at the end of the
                // frame.
                m_serverHierarchyChangedSet.Add(parent);
                return;
            }
            // S: server order, L: local order
            List<GameObject> childrenL = GetChildGameObjects(parent);
            if (childrenL == null)
            {
                return;
            }
            Transform parentTransform = sfObjectMap.Get().Get<Transform>(parent);
            int indexS = 0;
            IEnumerator<sfObject> iter = parent.Children.GetEnumerator();
            bool iterValid = iter.MoveNext();
            // Iterate the client children
            for (int indexL = 0; indexL < childrenL.Count; indexL++)
            {
                GameObject gameObjectL = childrenL[indexL];
                sfObject objL = sfObjectMap.Get().GetSFObject(gameObjectL);
                if (objL == null || !objL.IsSyncing)
                {
                    // gameObjectL is not synced. Ignore it.
                    continue;
                }
                bool moved = true;
                // Iterate the server children
                while (iterValid)
                {
                    sfObject objS = iter.Current;
                    if (objS == objL)
                    {
                        // We found the matching child. We don't need to move it.
                        moved = false;
                        indexS++;
                        iterValid = iter.MoveNext();
                        break;
                    }
                    GameObject gameObjectS = sfObjectMap.Get().Get<GameObject>(objS);
                    if (gameObjectS == null || gameObjectS.transform.parent != parentTransform)
                    {
                        // The server object has no game object or the game object has a different parent. The parent
                        // change will be sent when we sync the hierarchy for the new parent. Ignore it and continue
                        // iterating.
                        indexS++;
                        iterValid = iter.MoveNext();
                        continue;
                    }
                    // The child is not where we expected it. Either it needs to be moved or one or more other children
                    // need to be moved. eg. if the server has ABC and the client has CAB, you could move C to index 0,
                    // or you could move A to index 2, then B to index 2. We move the child if it is selected (since it
                    // needs to be selected to move it in the hierarchy), or if it has a different parent on the
                    // server.
                    if (objL.Parent != parent || Selection.Contains(gameObjectL))
                    {
                        break;
                    }
                    indexS++;
                    iterValid = iter.MoveNext();
                }
                if (!moved)
                {
                    continue;
                }
                if (!objL.IsLocked)
                {
                    if (objL.Parent != parent)
                    {
                        sfObject sceneObj = objL.FindAncestor(sfType.Scene);
                        parent.InsertChild(indexS, objL);
                        indexS++;
                        sfObject transformObj = sfObjectMap.Get().GetSFObject(gameObjectL.transform);
                        if (transformObj != null)
                        {
                            sfPropertyManager.Get().SendPropertyChanges(gameObjectL.transform,
                                (sfDictionaryProperty)transformObj.Property);
                        }
                    }
                    else
                    {
                        int oldIndex = parent.Children.IndexOf(objL);
                        if (oldIndex < indexS)
                        {
                            indexS--;
                        }
                        objL.SetChildIndex(indexS);
                        indexS++;
                    }
                }
                else
                {
                    // Put the object's parent in the server changed set so it is reverted to the server state at the
                    // end of the frame.
                    m_serverHierarchyChangedSet.Add(objL.Parent);
                }
            }
        }

        /**
         * Called when components are added or removed from a game object. Uploads the object if it became syncable
         * because a component that prevented it from syncing was removed.
         * 
         * @param   GameObject gameObject that had components added or removed.
         */
        private void OnAddOrRemoveComponents(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                // This will upload the game object if it became syncable.
                AddParentToUploadSet(gameObject);
            }
        }

        /**
         * Called when changes are made to a game object and possibly any descendants of the game object. Sends changes
         * for the game object and all of its descendants to the server, or reverts the changes if the objects are locked.
         * 
         * @param   GameObject gameObject that changed.
         */
        private void OnHierarchyStructureChange(GameObject gameObject)
        {
            SyncAll(gameObject, true, sfUndoManager.Get().IsHandlingUndoRedo);
        }

        /**
         * Sends all changes for a game object and its components to the server, or reverts it to the server state if
         * the object is locked. Optionally syncs changes for descendant game objects. Prefab source changes are only
         * synced if descendants are synced recursively, as prefab source changes can change descendants and requires
         * descendants to be synced. Game object creation and parent changes are synced in the next PreUpdate.
         * 
         * @param   GameObject gameObject to sync all changes for.
         * @param   bool if true, recursively syncs changes to all descendants, and syncs prefab source changes.
         * @param   bool relockObjects - if true, relocks game objects whose sfObjects are locked, and unlocks game
         *          objects whose sfObjects are unlocked. This is needed when syncing changes made by undo, which can
         *          mess up the lock state of game objects.
         */
        public void SyncAll(GameObject gameObject, bool recursive = false, bool relockObjects = false)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                AddParentToUploadSet(gameObject);
                return;
            }
            sfComponentTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfComponentTranslator>(
                sfType.Component);
            if (recursive)
            {
                SyncPrefabSource(gameObject);
                // When we connect the game object to a prefab, we destroy it and recreate it later, so check if the
                // game object was destroyed.
                if (gameObject.IsDestroyed())
                {
                    return;
                }
            }

            if (PrefabUtility.GetPrefabInstanceStatus(gameObject) == PrefabInstanceStatus.MissingAsset)
            {
                ksLog.Warning(this, gameObject.name + " is missing its prefab asset. SyncAll will not sync " +
                    "properties for game objects with missing prefab assets.", gameObject);
            }
            else
            {
                SyncProperties(gameObject);
            }
            obj.FlushPropertyChanges();// Make sure prefab path changes are sent now.
            translator.SyncComponentOrder(gameObject);
            translator.SyncComponents(gameObject);

            sfObject parent = GetParentObject(gameObject);
            if (parent != null)
            {
                m_localHierarchyChangedSet.Add(parent);
            }

            if (relockObjects)
            {
                if (obj.IsLocked)
                {
                    Lock(gameObject, obj);
                }
                else
                {
                    Unlock(gameObject);
                }
            }
            else if (obj.IsLocked && (gameObject.hideFlags & HideFlags.NotEditable) == 0)
            {
                sfUnityUtils.AddFlags(gameObject, HideFlags.NotEditable);
            }
            else if (!obj.IsLocked && (gameObject.hideFlags & HideFlags.NotEditable) != 0)
            {
                sfUnityUtils.RemoveFlags(gameObject, HideFlags.NotEditable);
            }
            if (recursive)
            {
                for (int i = 0; i < gameObject.transform.childCount; i++)
                {
                    SyncAll(gameObject.transform.GetChild(i).gameObject, true, relockObjects);
                }
                SyncDestroyedChildren(gameObject);
            }
        }

        /**
         * Sends prefab path and prefab child index changes to the server if the game object's prefab source changed,
         * or reverts the game object's prefab source to the server state if it is locked. This should be called on the
         * root of the prefab instance first, and then on the descendants.
         * 
         * @param   GameObject gameObject to sync prefab source for.
         */
        private void SyncPrefabSource(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                return;
            }
            if (PrefabUtility.GetPrefabInstanceStatus(gameObject) == PrefabInstanceStatus.MissingAsset)
            {
                // Do not sync the prefab path for a broken prefab as we cannot determine what the prefab should be and
                // we don't want to unlink the prefab for other users who are not missing the prefab asset.
                return;
            }
            string prefabPath;
            int childIndex;
            GetPrefabInfo(gameObject, out prefabPath, out childIndex);
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            sfBaseProperty prop;
            string currentPath = properties.TryGetField(sfProp.Path, out prop) ? (string)prop : null;
            if (currentPath == prefabPath || (string.IsNullOrEmpty(currentPath) && string.IsNullOrEmpty(prefabPath)))
            {
                return;
            }
            if (obj.IsLocked)
            {
                // Revert to the server state.
                OnPrefabPathChange(gameObject, prop);
            }
            else if (string.IsNullOrEmpty(prefabPath))
            {
                properties.RemoveField(sfProp.Path);
                properties.RemoveField(sfProp.Index);
            }
            else
            {
                properties[sfProp.Path] = prefabPath;
                if (childIndex < 0)
                {
                    properties.RemoveField(sfProp.Index);
                }
                else
                {
                    properties[sfProp.Index] = childIndex;
                }
            }
        }

        /**
         * Called when a game object's prefab path is changed by the server.
         * 
         * @param   GameObject gameObject whose prefab path changed.
         * @param   sfBaseProperty property that changed. Null if the game object no longer has a prefab path.
         */
        public void OnPrefabPathChange(GameObject gameObject, sfBaseProperty property)
        {
            if (property == null)
            {
                // Unpack the game object until it is no longer the root of a prefab instance. We unpack one level at a
                // time in case the descendants are still part of a nested prefab instance.
                while (PrefabUtility.IsOutermostPrefabInstanceRoot(gameObject))
                {
                    PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.OutermostRoot,
                        InteractionMode.AutomatedAction);
                    sfHierarchyWatcher.Get().MarkHierarchyStale();
                }
                sfMissingPrefab missingPrefab = gameObject.GetComponent<sfMissingPrefab>();
                if (missingPrefab != null)
                {
                    UObject.DestroyImmediate(missingPrefab);
                    ksLinkedList<sfNotification> notifications = sfNotificationManager.Get()
                        .GetNotifications(gameObject);
                    if (notifications != null && notifications.Count > 0)
                    {
                        foreach (sfNotification notification in notifications)
                        {
                            if (notification.Category == sfNotificationCategory.MissingPrefab)
                            {
                                sfNotificationManager.Get().RemoveNotificationFrom(notification, gameObject);
                            }
                        }
                    }
                }
                else
                {
                    // Reapply properties next frame
                    m_applyPropertiesSet.Add(gameObject);
                }
            }
            else
            {
                string path = (string)property;
                string currentPath = "";
                // Unpack the prefab until we get the correct prefab or are no longer the root of a prefab instance.
                while (PrefabUtility.IsOutermostPrefabInstanceRoot(gameObject))
                {
                    GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);
                    if (prefab != null)
                    {
                        currentPath = AssetDatabase.GetAssetPath(prefab);
                        if (path == currentPath)
                        {
                            // We unpacked to the correct prefab.
                            // Reapply properties next frame
                            m_applyPropertiesSet.Add(gameObject);
                            return;
                        }
                    }
                    PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.OutermostRoot,
                        InteractionMode.AutomatedAction);
                    sfHierarchyWatcher.Get().MarkHierarchyStale();
                }
                // Destroy the game object and recreate it as a prefab instance.
                DestroyAndRecreate(gameObject);
            }
        }

        /**
         * Destroys a game object and adds it's sfObject to a list to be recreated in PreUpdate. This is used when we
         * need to recreate a game object as a different prefab instance. We wait until PreUpdate before recreating it
         * to ensure we have the updated properties--including the updated prefab path and child index--for this object
         * and its children.
         * 
         * @param   GameObject gameObject to destroy and recreate.
         */
        private void DestroyAndRecreate(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }
            sfObject root = sfObjectMap.Get().GetSFObject(gameObject);
            if (root == null)
            {
                return;
            }
            List<GameObject> toDetach = new List<GameObject>();
            sfUnityUtils.ForEachDescendant(gameObject, (GameObject child) =>
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(child);
                // Detach unsynced descendants and reattach them when the game object and its descendants are
                // recreated.
                if (obj == null || !obj.IsSyncing)
                {
                    if (!sfLockManager.Get().IsLockObject(child))
                    {
                        m_reattachList.Add(new AttachmentInfo(child));
                        toDetach.Add(child);
                    }
                    return false;
                }
                // Detach descendants with a different parent on the server, and put the parent in the server
                // hierarchy changed set to restore its children.
                sfObject parent = sfObjectMap.Get().GetSFObject(child.transform.parent);
                if (obj.Parent != parent && obj.Parent != root && !obj.Parent.IsDescendantOf(root))
                {
                    m_serverHierarchyChangedSet.Add(obj.Parent);
                    toDetach.Add(child);
                }
                return true;
            });
            // Detach descendants we don't want destroyed before destroying the game object.
            for (int i = 0; i < toDetach.Count; i++)
            {
                sfComponentUtils.SetParent(toDetach[i], null);
            }
            sfObject obj = RemoveMapping(gameObject);
            if (obj != null)
            {
                obj.ForEachDescendant((sfObject child) =>
                {
                    RemoveMapping(child);
                    return true;
                });
            }
            DestroyGameObject(gameObject);
            // Recreate it in PreUpdate after we receive all property changes
            m_recreateList.Add(root);
        }

        /**
         * Recreates the game objects for sfObjects in the recreate list.
         */
        private void RecreateGameObjects()
        {
            if (m_recreateList.Count == 0)
            {
                return;
            }

            foreach (sfObject obj in m_recreateList)
            {
                if (!sfObjectMap.Get().Contains(obj))
                {
                    OnCreate(obj, obj.Parent == null ? -1 : obj.Parent.Children.IndexOf(obj));
                    GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
                    if (obj.IsLockRequested)
                    {
                        // If we have requested the lock on this object, the old game object was selected before it was
                        // destroyed. Select the new game object.
                        if (gameObject == null)
                        {
                            obj.ReleaseLock();
                        }
                        else
                        {
                            List<UObject> selection = new List<UObject>(Selection.objects);
                            selection.Add(gameObject);
                            Selection.objects = selection.ToArray();
                        }
                    }
                    if (!obj.IsLocked && gameObject != null && (gameObject.hideFlags & HideFlags.NotEditable) != 0)
                    {
                        // Sometimes the prefabs are locked which causes the prefab instances to be locked, so we need
                        // to unlock them.
                        foreach (GameObject go in sfUnityUtils.IterateSelfAndDescendants(gameObject))
                        {
                            sfUnityUtils.RemoveFlags(go, HideFlags.NotEditable);
                        }
                    }
                }
            }
            m_recreateList.Clear();

            // Reattach unsynced objects that were detached from recreated objects.
            foreach (AttachmentInfo attachment in m_reattachList)
            {
                attachment.Restore();
            }
            m_reattachList.Clear();
        }

        /**
         * Temporarily unlocks a game object, and relocks it on the next update.
         * 
         * @param   GameObject gameObject to unlock temporarily.
         */
        public void TempUnlock(GameObject gameObject)
        {
            if ((gameObject.hideFlags & HideFlags.NotEditable) != HideFlags.None)
            {
                m_relockObjects = true;// Relock objects on the next update
                sfUnityUtils.RemoveFlags(gameObject, HideFlags.NotEditable);
                m_tempUnlockedObjects.Add(gameObject);
            }
        }

        /**
         * Relocks all game objects in a prefab instance on the next PreUpdate.
         * 
         * @param   GameObject gameObject in prefab to relock.
         */
        public void RelockPrefabNextPreUpdate(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }
            sfUnityUtils.ForEachInPrefab(gameObject, (GameObject go) =>
            {
                m_tempUnlockedObjects.Add(go);
                return true;
            });
            m_relockObjects = true;
        }

        /**
         * Sends property changes for a game object and its components to the server. Reverts them to the server state
         * if the object is locked.
         * 
         * @param   GameObject gameObject to sync properties for.
         * @param   bool recursive - if true, will recursively sync properties for child game objects.
         */
        public void SyncProperties(GameObject gameObject, bool recursive = false)
        {
            if (gameObject == null)
            {
                return;
            }
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                return;
            }
            if (obj.IsLocked)
            {
                sfPropertyManager.Get().ApplyProperties(gameObject, (sfDictionaryProperty)obj.Property);
            }
            else
            {
                sfPropertyManager.Get().SendPropertyChanges(gameObject, (sfDictionaryProperty)obj.Property);
            }

            foreach (Component component in gameObject.GetComponents<Component>())
            {
                obj = sfObjectMap.Get().GetSFObject(component);
                if (obj == null || !obj.IsSyncing)
                {
                    continue;
                }
                if (obj.IsLocked)
                {
                    sfPropertyManager.Get().ApplyProperties(component, (sfDictionaryProperty)obj.Property);
                }
                else
                {
                    sfPropertyManager.Get().SendPropertyChanges(component, (sfDictionaryProperty)obj.Property);
                }
            }

            if (recursive)
            {
                for (int i = 0; i < gameObject.transform.childCount; i++)
                {
                    SyncProperties(gameObject.transform.GetChild(i).gameObject);
                }
            }
        }

        /**
         * Destroys server objects for destroyed children of a game object. Recreates the game object if the objects
         * are locked.
         * 
         * @param   GameObject gameObject to sync destroyed children for.
         */
        public void SyncDestroyedChildren(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject.transform);
            if (obj == null || !obj.IsSyncing)
            {
                return;
            }
            foreach (sfObject childObj in obj.Children)
            {
                if (childObj.Type == sfType.GameObject && sfObjectMap.Get().Get<GameObject>(childObj).IsDestroyed())
                {
                    // Sync changed hierarchies before deleting the object in case descendants of the object were
                    // reparented.
                    SyncChangedHierarchies();
                    SyncDeletedObject(childObj);
                }
            }
        }

        /**
         * Applies the server state to a game object and its components.
         * 
         * @param   sfObject obj for the game object to apply server state for.
         * @param   bool recursive - if true, will also apply server state to descendants of the game object.
         */
        public void ApplyServerState(sfObject obj, bool recursive = false)
        {
            if (obj.Type != sfType.GameObject)
            {
                return;
            }
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            if (gameObject == null)
            {
                OnCreate(obj, obj.Parent.Children.IndexOf(obj));
                return;
            }
            m_serverHierarchyChangedSet.Add(obj.Parent);
            sfPropertyManager.Get().ApplyProperties(gameObject, (sfDictionaryProperty)obj.Property);
            sfComponentTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfComponentTranslator>(
                sfType.Component);
            int index = -1;
            foreach (sfObject child in obj.Children)
            {
                index++;
                if (child.Type != sfType.Component)
                {
                    continue;
                }
                Component component = sfObjectMap.Get().Get<Component>(child);
                if (component == null)
                {
                    translator.OnCreate(child, index);
                }
                else
                {
                    sfPropertyManager.Get().ApplyProperties(component, (sfDictionaryProperty)child.Property);
                    if (recursive && component is Transform)
                    {
                        foreach (sfObject grandChild in child.Children)
                        {
                            if (grandChild.Type == sfType.GameObject)
                            {
                                ApplyServerState(grandChild, true);
                            }
                        }
                    }
                }
            }
            // Destroy unsynced components
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (!sfObjectMap.Get().Contains(component) && translator.IsSyncable(component))
                {
                    translator.DestroyComponent(component);
                }
            }
            if (recursive)
            {
                // Destroy unsynced children and reparent children with different server parents.
                sfObject transformObj = sfObjectMap.Get().GetSFObject(gameObject.transform);
                for (int i = gameObject.transform.childCount - 1; i >= 0; i--)
                {
                    GameObject child = gameObject.transform.GetChild(i).gameObject;
                    sfObject childObj = sfObjectMap.Get().GetSFObject(child);
                    if (childObj == null)
                    {
                        if (IsSyncable(child))
                        {
                            DestroyGameObject(child);
                        }
                    }
                    else if (childObj.Parent != transformObj)
                    {
                        m_serverHierarchyChangedSet.Add(childObj.Parent);
                    }
                }
            }
        }

        /**
         * Called when the user completes dragging objects in the hierarchy. Re-adds HideFlags.NotEditable on the next
         * pre update to objects that were temporarily made editable to make dragging work. If the target is null, adds
         * the scene's hierarchy sfObject to the local hierarchy changed set to sync root game object order changes in
         * the next pre update.
         * 
         * @param   GameObject target the objects were dragged onto.
         * @param   Scene scene the objects were dragged onto.
         */
        private void OnHierarchyDragComplete(GameObject target, Scene scene)
        {
            // Relock objects on the next update that were temporarily unlocked to make dragging work. If we try to
            // relock them now, Unity will cancel reparenting if the parent is locked.
            m_relockObjects = true;

            if (target == null)
            {
                // Unity doesn't have an event for root object order changes, so we detect it by detecting a drag
                // without a target game object. This won't detect root order changes made programmatically by plugins.
                // Unity does fire a parent change event when undoing a root order change, so we don't need to do
                // anything extra to detect that.
                //TODO: In Unity 6 there is a new ObjectChangeKind.ChangeRootOrder event we can use.
                sfSceneTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfSceneTranslator>(
                    sfType.Scene);
                sfObject obj = translator.GetHierarchyObject(scene);
                if (obj != null)
                {
                    m_localHierarchyChangedSet.Add(obj);
                }
            }
#if !UNITY_2022_2_OR_NEWER
            else
            {
                // Unity 2022.1 and below do not have child reorder events, so we detect it by checking if any of the
                // dragged objects are already a parent of the target (their parent hasn't updated yet).
                foreach (UObject uobj in DragAndDrop.objectReferences)
                {
                    GameObject gameObject = uobj as GameObject;
                    if (gameObject != null && gameObject.transform.parent == target.transform)
                    {
                        sfUndoManager.Get().Record(new sfUndoReorderChildrenOperation(target));
                        sfUnityEventDispatcher.Get().InvokeOnReorderChildren(target);
                    }
                }
            }
#endif
        }

        /**
         * Validates a hierarchy drag operation. A drag operation is allowed if the target is not fully locked and all
         * dragged objects are unlocked.
         * 
         * @param   GameObject target parent for the dragged objects.
         * @param   int childIndex the dragged objects will be inserted at.
         * @return  bool true if the drag should be allowed.
         */
        private bool ValidateHierarchyDrag(GameObject target, int childIndex)
        {
            sfObject targetObj = sfObjectMap.Get().GetSFObject(target);
            // If the target is locked, temporarily unlock it. We need to unlock partially locked objects to allow
            // children to be added to them. We need to unlock fully locked objects as well because keeping them
            // locked interferes with drag target detection and causes flickering.
            if (targetObj != null && targetObj.IsLocked &&
                (target.hideFlags & HideFlags.NotEditable) != HideFlags.None)
            {
                sfUnityUtils.RemoveFlags(target, HideFlags.NotEditable);
                m_tempUnlockedObjects.Add(target);
            }

            if (targetObj != null && targetObj.IsFullyLocked)
            {
                return false;
            }
            // Don't allow the drag if any of the dragged objects are locked, unless they are assets.
            foreach (UObject uobj in DragAndDrop.objectReferences)
            {
                if (sfLoader.Get().IsAsset(uobj))
                {
                    continue;
                }
                sfObject obj = sfObjectMap.Get().GetSFObject(uobj);
                if (obj != null && obj.IsLocked)
                {
                    return false;
                }
                GameObject gameObject = uobj as GameObject;
                if (gameObject == null)
                {
                    continue;
                }
                // Disallow dragging a missing prefab that is not the root of the prefab.
                sfMissingPrefab missingPrefab = gameObject.GetComponent<sfMissingPrefab>();
                if (missingPrefab != null && missingPrefab.ChildIndex != -1)
                {
                    return false;
                }
            }
            return true;
        }

        /**
         * Adds a game object's parent's sfObject to the set of objects with child game objects to upload.
         * 
         * @param   GameObject gameObject
         */
        private void AddParentToUploadSet(GameObject gameObject)
        {
            if (!IsSyncable(gameObject))
            {
                return;
            }
            sfObject parent = GetParentObject(gameObject);
            if (parent != null && parent.IsSyncing)
            {
                m_parentsWithNewChildren.Add(parent);
            }
        }

        /**
         * Gets the sfObject for a game object's parent. This is either a hierarchy object if the game object is a root
         * object, or a transform component object.
         * 
         * @param   GameObject gameObject to get parent object for.
         * @return  sfObject parent object.
         */
        private sfObject GetParentObject(GameObject gameObject)
        {
            if (gameObject.transform.parent == null)
            {
                // The parent object is a hierarchy object
                sfSceneTranslator sceneTranslator = sfObjectEventDispatcher.Get()
                    .GetTranslator<sfSceneTranslator>(sfType.Hierarchy);
                return sceneTranslator.GetHierarchyObject(gameObject.scene);
            }
            else
            {
                // The parent object is a transform
                return sfObjectMap.Get().GetSFObject(gameObject.transform.parent);
            }
        }

        /**
         * Uploads new child game objects of objects in the parents-with-new-children set to the server.
         */
        private void UploadGameObjects()
        {
            if (m_parentsWithNewChildren.Count == 0)
            {
                return;
            }
            sfSession session = SceneFusion.Get().Service.Session;
            List<sfObject> uploadList = new List<sfObject>();
            foreach (sfObject parent in m_parentsWithNewChildren)
            {
                if (!parent.IsSyncing)
                {
                    continue;
                }
                // User an enumerator to iterate the chilren since they are stored in a linked list and index iteration
                // is slow.
                IEnumerator<sfObject> childIter = parent.Children.GetEnumerator();
                bool childIterHasValue = childIter.MoveNext();
                int index = 0; // Child index of first uploaded object
                // Check for new child game objects to upload
                foreach (GameObject gameObject in IterateChildGameObjects(parent))
                {
                    sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
                    if (obj != null && obj.IsSyncing)
                    {
                        if (!childIterHasValue)
                        {
                            continue;
                        }
                        // Objects uploaded together must be in a continuous sequence, so when we find an object that
                        // is already uploaded, upload the upload list if it's non-empty.
                        if (uploadList.Count > 0)
                        {
                            session.Create(uploadList, parent, index);
                            index += uploadList.Count;
                            uploadList.Clear();
                        }

                        // Advance the child iterator to the next child after obj.
                        while (childIterHasValue && childIter.Current != obj)
                        {
                            index++;
                            childIterHasValue = childIter.MoveNext();
                        }
                        if (childIterHasValue)
                        {
                            index++;
                            childIterHasValue = childIter.MoveNext();
                        }
                    }
                    else if ((obj == null || !obj.IsDeletePending) && IsSyncable(gameObject))
                    {
                        // Sometimes Unity destroys and recreates a game object without firing an event for the
                        // destroyed object. This happens when a new prefab is created from a game object and there
                        // were broken prefab instances for that prefab. We check for this by checking if there was a
                        // deleted game object at the same index as the new game object.
                        bool isReplacement = childIterHasValue && !childIter.Current.IsDeletePending &&
                                sfObjectMap.Get().Get<GameObject>(childIter.Current).IsDestroyed();

                        // If the parent or replaced object is locked, delete the new game object.
                        if (parent.IsFullyLocked || (isReplacement && childIter.Current.IsLocked))
                        {
                            DestroyGameObject(gameObject);

                            if (isReplacement)
                            {
                                // Recreate the replaced object.
                                OnCreate(childIter.Current, index);
                            }
                        }
                        else
                        {
                            // When Unity replaces a broken prefab instance, the transform is not overriden so it gets
                            // moved to default prefab location. We detect this and reapply the transform values from
                            // the object is replaced.
                            if (isReplacement && PrefabUtility.IsPartOfPrefabInstance(gameObject))
                            {
                                SerializedObject so = sfPropertyManager.Get()
                                    .GetSerializedObject(gameObject.transform);
                                SerializedProperty sprop = so.FindProperty(sfProp.Position);
                                if (sprop != null && !sprop.prefabOverride)
                                {
                                    sfObject transformObj = GetTransformObj(childIter.Current);
                                    if (transformObj != null)
                                    {
                                        sfPropertyManager.Get().ApplyProperties(gameObject.transform, 
                                            (sfDictionaryProperty)transformObj.Property);
                                        sfPropertyManager.Get().ApplySerializedProperties(gameObject.transform);
                                    }
                                }
                            }

                            // Found an object to upload. Create an sfObject and add it to the upload list.
                            obj = CreateObject(gameObject);
                            if (obj != null)
                            {
                                uploadList.Add(obj);
                            }

                            if (isReplacement)
                            {
                                // Delete the replaced object.
                                SyncDeletedObject(childIter.Current);
                            }
                        }
                        if (isReplacement)
                        {
                            childIterHasValue = childIter.MoveNext();
                        }
                    }
                }
                // Upload the objects
                if (uploadList.Count > 0)
                {
                    session.Create(uploadList, parent, index);
                    uploadList.Clear();
                }
            }
            m_parentsWithNewChildren.Clear();
        }

        /**
         * Gets the transform object from a game object sfObject by returning the first child component sfObject, which
         * is always the transform.
         * 
         * @param   sfObject obj - game object sfObject to get transform sfObject for.
         * @return  sfObject transform object.
         */
        private sfObject GetTransformObj(sfObject obj)
        {
            foreach (sfObject child in obj.Children)
            {
                if (child.Type == sfType.Component)
                {
                    return child;
                }
            }
            return null;
        }

        /**
         * Gets the child game objects of an sfObject's scene or transform.
         * 
         * @param   sfObject parent for the scene or transform to get child game objects from.
         * @return  List<GameObject> children of the object. Null if the transform could not be found.
         */
        public List<GameObject> GetChildGameObjects(sfObject parent)
        {
            if (parent == null)
            {
                return null;
            }
            if (parent.Type == sfType.Hierarchy)
            {
                sfSceneTranslator sceneTranslator = sfObjectEventDispatcher.Get()
                    .GetTranslator<sfSceneTranslator>(sfType.Hierarchy);
                Scene scene = sceneTranslator.GetScene(parent);
                if (scene.isLoaded)
                {
                    List<GameObject> children = new List<GameObject>();
                    scene.GetRootGameObjects(children);
                    return children;
                }
            }
            else if (parent.Type == sfType.Component)
            {
                Transform transform = sfObjectMap.Get().Get<Transform>(parent);
                if (transform != null)
                {
                    List<GameObject> children = new List<GameObject>();
                    foreach (Transform child in transform)
                    {
                        children.Add(child.gameObject);
                    }
                    return children;
                }
            }
            return null;
        }

        /**
         * Iterates the child game objects of an sfObject's scene or transform.
         * 
         * @param   sfObject parent for the scene or transform to iterate.
         * @return  IEnumerable<GameObject>
         */
        public IEnumerable<GameObject> IterateChildGameObjects(sfObject parent)
        {
            if (parent.Type == sfType.Hierarchy)
            {
                sfSceneTranslator sceneTranslator = sfObjectEventDispatcher.Get()
                    .GetTranslator<sfSceneTranslator>(sfType.Hierarchy);
                Scene scene = sceneTranslator.GetScene(parent);
                if (scene.isLoaded)
                {
                    List<GameObject> roots = new List<GameObject>();
                    scene.GetRootGameObjects(roots);
                    foreach (GameObject gameObject in roots)
                    {
                        yield return gameObject;
                    }
                }
            }
            else if (parent.Type == sfType.Component)
            {
                Transform transform = sfObjectMap.Get().Get<Transform>(parent);
                if (transform != null)
                {
                    foreach (Transform child in transform)
                    {
                        yield return child.gameObject;
                    }
                }
            }
        }

        /**
         * Called when a game object's parent is changed by another user.
         * 
         * @param   sfObject obj whose parent changed.
         * @param   int childIndex of the object. -1 if the object is a root.
         */
        public override void OnParentChange(sfObject obj, int childIndex)
        {
            if (obj.Parent != null)
            {
                // Apply the change at the end of the frame.
                m_serverHierarchyChangedSet.Add(obj.Parent);
            }
        }

        /**
         * Creates an sfObject for a uobject. Does not upload or create properties for the object.
         *
         * @param   UObject uobj to create sfObject for.
         * @param   sfObject outObj created for the uobject.
         * @return  bool true if the uobject was handled by this translator.
         */
        public override bool TryCreate(UObject uobj, out sfObject outObj)
        {
            outObj = null;
            GameObject gameObject = uobj as GameObject;
            if (gameObject == null)
            {
                return false;
            }
            if (IsSyncable(gameObject))
            {
                outObj = new sfObject(sfType.GameObject, new sfDictionaryProperty());
                AddMapping(outObj, gameObject);
            }
            return true;
        }

        /**
         * Recusively creates sfObjects for a game object and its children.
         * 
         * @param   GameObject gameObject to create sfObject for.
         * @return  sfObject for the gameObject.
         */
        public sfObject CreateObject(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetOrCreateSFObject(gameObject, sfType.GameObject);
            if (obj.IsSyncing)
            {
                return null;
            }
            m_instanceIdToSFObjectMap[gameObject.GetInstanceID()] = obj;

            Guid guid = sfGuidManager.Get().GetGuid(gameObject);
            if (sfGuidManager.Get().GetGameObject(guid) != gameObject)
            {
                // If the game object's guid is mapped to a different game object, this is a duplicate object.
                // Duplicate objects can be created when a user deletes a locked object which is recreated because it
                // was locked, and then undoes the delete.
                List<GameObject> toDetach = new List<GameObject>();
                sfUnityUtils.ForEachDescendant(gameObject, (GameObject child) =>
                {
                    sfObject childObj = sfObjectMap.Get().GetSFObject(child);
                    if (childObj != null && childObj.IsSyncing && sfObjectMap.Get().Get<GameObject>(childObj) == child)
                    {
                        // This descendant is a synced object and not a duplicate. Detach it and put the parent in the
                        // server hierarchy changed set to restore its children.
                        toDetach.Add(child);
                        m_serverHierarchyChangedSet.Add(childObj.Parent);
                        return false;
                    }
                    RemoveMapping(child);
                    return true;
                });
                for (int i = 0; i < toDetach.Count; i++)
                {
                    sfComponentUtils.SetParent(toDetach[i], null);
                }
                // Destroy the duplicate object.
                RemoveMapping(gameObject);
                DestroyGameObject(gameObject);
                return null;
            }

            // If a user duplicates a locked object, the duplicate will be locked, so we need to unlock it.
            // We detect this by checking the hideflags, except for prefab instances which will have their prefab's hide
            // flags, so we always try to find a lock object to destroy on prefab instances.
            if ((gameObject.hideFlags & HideFlags.NotEditable) != 0 ||
                PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                Unlock(gameObject, true);
            }

            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            string path;
            int childIndex;
            bool needsPrefabReconnect = RemoveInvalidMissingPrefab(gameObject);
            GetPrefabInfo(gameObject, out path, out childIndex);
            if (!string.IsNullOrEmpty(path))
            {
                properties[sfProp.Path] = path;
                if (childIndex >= 0)
                {
                    properties[sfProp.Index] = childIndex;
                }
            }
            else if (PrefabUtility.GetPrefabInstanceStatus(gameObject) == PrefabInstanceStatus.MissingAsset)
            {
                ksLog.Warning(this, "Found prefab instance " + gameObject.name + " with a missing prefab asset. " +
                        "The gane object will be a non-prefab for other users.",
                        gameObject);
                if (OnMissingPrefab != null)
                {
                    OnMissingPrefab(gameObject);
                }
            }

            if (Selection.Contains(gameObject))
            {
                obj.RequestLock();
            }

            properties[sfProp.Guid] = guid.ToByteArray();
            sfPropertyManager.Get().CreateProperties(gameObject, properties);

            // Create component child objects
            sfComponentTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfComponentTranslator>(
                sfType.Component);
            bool isFirst = true;
            foreach (Component component in GetComponents(gameObject))
            {
                if (translator.IsSyncable(component))
                {
                    // If the component's game object is not this game object, this is a prefab component that was
                    // removed from the prefab instance.
                    bool isRemoved = component.gameObject != gameObject;
                    sfObject child = translator.CreateObject(component, isFirst, isRemoved);
                    isFirst = false;
                    if (child != null)
                    {
                        obj.AddChild(child);
                    }
                }
            }

            InvokeOnLockStateChange(obj, gameObject);

            if (needsPrefabReconnect)
            {
                // The game object had a sfMissingPrefab component for a prefab that exists. Recreate the game object
                // as a prefab instance.
                DestroyAndRecreate(gameObject);
            }
            return obj;
        }

        /**
         * Removes invalid missing prefab components from a game object. A missing component is invalid if it has
         * child indexes and the parent is not part of the prefab the missing prefab component is for.
         * 
         * @param   GameObject gameObject to check for and remove missing prefab components from.
         * @return  bool true if a sfMissingPrefab component was removed because the prefab exists.
         */
        private bool RemoveInvalidMissingPrefab(GameObject gameObject)
        {
            sfMissingPrefab missingPrefab = gameObject.GetComponent<sfMissingPrefab>();
            if (missingPrefab == null)
            {
                return false;
            }
            // If the missing prefab has no child index, check if the prefab exists.
            if (missingPrefab.ChildIndex < 0)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(missingPrefab.PrefabPath) != null)
                {
                    return true;
                }
                CreateMissingPrefabNotification(missingPrefab);
                return false;
            }
            // If the game object has no parent or the parent is not part of the same prefab path, destroy the missing
            // prefab component.
            if (gameObject.transform.parent == null || 
                GetPrefabPath(gameObject.transform.parent.gameObject) != missingPrefab.PrefabPath)
            {
                UObject.DestroyImmediate(missingPrefab);
                return false;
            }
            CreateMissingPrefabNotification(missingPrefab);
            return false;
        }

        /**
         * Creates a notification for a missing prefab.
         * 
         * @param   sfMissingPrefab missingPrefab to create notification for.
         */
        private void CreateMissingPrefabNotification(sfMissingPrefab missingPrefab)
        {
            if (missingPrefab.ChildIndex >= 0 &&
                AssetDatabase.LoadAssetAtPath<GameObject>(missingPrefab.PrefabPath) != null)
            {
                sfNotification.Create(sfNotificationCategory.MissingPrefab,
                    "Unable to find child prefab in '" + missingPrefab.PrefabPath + "'.", missingPrefab.gameObject);
            }
            else
            {
                sfNotification notification = sfNotification.Create(sfNotificationCategory.MissingPrefab,
                    "Unable to load prefab '" + missingPrefab.PrefabPath + "'.", missingPrefab.gameObject);
                // If there's only 1 object this is a new notification. Add it to the map.
                if (notification.Objects.Count == 1)
                {
                    m_missingPrefabNotificationMap[missingPrefab.PrefabPath] = notification;
                }
            }
        }

        /**
         * Called when assets are imported. Removes sfMissingPrefab components from prefab assets. Replaces
         * missing prefabs with the new prefab if it becomes available during a session.
         * 
         * @param   string[] assets that were created.
         */
        private void HandleImportAssets(string[] assets)
        {
            foreach (string path in assets)
            {
                if (path.EndsWith(".prefab"))
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                    {
                        bool changed = false;
                        foreach (sfMissingPrefab missingPrefab in prefab.GetComponentsInChildren<sfMissingPrefab>())
                        {
                            UObject.DestroyImmediate(missingPrefab, true);
                            changed = true;
                        }
                        if (changed)
                        {
                            sfPrefabLocker.Get().AllowSave(path);
                        }
                    }
                }
                if (SceneFusion.Get().Service.IsConnected)
                {
                    // Prefab path could end in ".prefab" or ".fbx".
                    if (m_missingPrefabNotificationMap.ContainsKey(path))
                    {
                        // Replacing missing prefabs will crash Unity if we do it now and one of the new prefab was
                        // created from a missing prefab instance, so we do it from delayCall.
                        EditorApplication.delayCall += () =>
                        {
                            ReplaceMissingPrefabs(path);
                        };
                    }
                }
            }
        }

        /**
         * Destroys and recreates missing prefabs instances as prefabs instances for the prefab at the given path.
         * 
         * @param   string path to prefab to replace missing prefab instances for.
         */
        private void ReplaceMissingPrefabs(string path)
        {
            sfNotification notification;
            if (m_missingPrefabNotificationMap.Remove(path, out notification))
            {
                int count = 0;
                foreach (GameObject gameObject in notification.Objects)
                {
                    if (gameObject != null)
                    {
                        DestroyAndRecreate(gameObject);
                        count++;
                    }
                }
                notification.Clear();
                if (count > 0)
                {
                    ksLog.Info(this, "Replaced " + count + " missing prefab(s) with '" + path + "'.");
                }
            }
        }

        /**
         * Called when a game object is created by another user.
         * 
         * @param   sfObject obj that was created.
         * @param   int childIndex of the new object. -1 if the object is a root.
         */
        public override void OnCreate(sfObject obj, int childIndex)
        {
            if (obj.Parent == null)
            {
                ksLog.Error(this, "GameObject sfObject has no parent.");
                return;
            }
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            if (gameObject != null)
            {
                ksLog.Warning(this, "OnCreate called for sfObject that already has a game object '" +
                    gameObject.name + "'.");
                return;
            }
            if (obj.Parent.Type == sfType.Hierarchy)
            {
                sfSceneTranslator sceneTranslator = sfObjectEventDispatcher.Get()
                    .GetTranslator<sfSceneTranslator>(sfType.Hierarchy);
                Scene scene = sceneTranslator.GetScene(obj.Parent);
                if (scene.isLoaded)
                {
                    gameObject = InitializeGameObject(obj, scene);
                }
            }
            else if (obj.Parent.Type == sfType.Component)
            {
                Transform transform = sfObjectMap.Get().Get<Transform>(obj.Parent);
                if (transform != null)
                {
                    gameObject = InitializeGameObject(obj, transform.gameObject.scene);
                }
            }
            else
            {
                return;
            }
            // If the game object is the last child it will be in the correct location, unless it is a root prefab.
            if (gameObject != null && (childIndex != obj.Parent.Children.Count - 1 ||
                (PrefabUtility.IsPartOfPrefabInstance(gameObject) && gameObject.transform.parent == null)))
            {
                m_serverHierarchyChangedSet.Add(obj.Parent);
            }
        }

        /**
         * Creates or finds a game object for an sfObject and initializes it with server values. Recursively
         * initializes children.
         * 
         * @param   sfObject obj to initialize game object for.
         * @param   Scene scene the game object belongs to.
         * @return  GameObject gameObject for the sfObject.
         */
        public GameObject InitializeGameObject(sfObject obj, Scene scene)
        {
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            // Try get the prefab path and child index properties
            string path = null;
            int childIndex = -1;
            sfBaseProperty property;
            if (properties.TryGetField(sfProp.Path, out property))
            {
                path = (string)property;
                if (properties.TryGetField(sfProp.Index, out property))
                {
                    childIndex = (int)property;
                }
            }

            // Try get the game object by its guid
            Guid guid = new Guid((byte[])properties[sfProp.Guid]);
            GameObject gameObject = sfGuidManager.Get().GetGameObject(guid);
            if (gameObject != null && !ValidateGameObject(gameObject, path, childIndex, obj.Parent))
            {
                // The game object is not the correct prefab. Remove it from the guid manager and don't use it.
                sfGuidManager.Get().Remove(gameObject);
                gameObject = null;
            }
            if (gameObject == null && childIndex >= 0)
            {
                // Try find the child from the prefab
                gameObject = FindChild(obj.Parent, childIndex);
                if (gameObject != null)
                {
                    if (!ValidateGameObject(gameObject, path, childIndex))
                    {
                        gameObject = null;
                    }
                    else
                    {
                        sfGuidManager.Get().SetGuid(gameObject, guid);
                    }
                }
            }

            // Create the game object if we couldn't find it by its guid
            sfMissingPrefab missingPrefab = null;
            bool isNewPrefab = false;
            if (gameObject == null)
            {
                bool isMissingPrefab = false;
                if (path != null)
                {
                    if (childIndex < 0)
                    {
                        gameObject = sfUnityUtils.InstantiatePrefab(scene, path);
                        isNewPrefab = gameObject != null;
                    }
                    // Starting in 2022.2 it is possible to delete a prefab instance child.
#if UNITY_2022_2_OR_NEWER
                    else
                    {
                        Transform parentTransform = sfObjectMap.Get().Get<Transform>(obj.Parent);
                        if (parentTransform != null)
                        {
                            gameObject = RestoreChildPrefab(parentTransform.gameObject, childIndex);
                            // Restoring a deleted prefab child resets the hideflags, so we need to relock the game
                            // objects in the prefab instance.
                            if (obj.IsLocked && gameObject != null)
                            {
                                RelockPrefabNextPreUpdate(gameObject);
                            }
                        }
                    }
#endif
                    if (gameObject == null)
                    {
                        gameObject = new GameObject();
                        SceneManager.MoveGameObjectToScene(gameObject, scene);
                        isMissingPrefab = true;
                    }
                }
                else
                {
                    gameObject = new GameObject();
                    SceneManager.MoveGameObjectToScene(gameObject, scene);
                }
                if (isMissingPrefab)
                {
                    missingPrefab = gameObject.AddComponent<sfMissingPrefab>();
                    missingPrefab.PrefabPath = path;
                    missingPrefab.ChildIndex = childIndex;
                }
                sfGuidManager.Get().SetGuid(gameObject, guid);
                sfUI.Get().MarkSceneViewStale();
            }
            else
            {
                // Send a lock request if we have the game object selected
                if (Selection.Contains(gameObject))
                {
                    obj.RequestLock();
                }

                if (path != null)
                {
                    missingPrefab = gameObject.GetComponent<sfMissingPrefab>();
                }
            }
            AddMapping(obj, gameObject);
            if (missingPrefab != null)
            {
                CreateMissingPrefabNotification(missingPrefab);
            }

            sfPropertyManager.Get().ApplyProperties(gameObject, properties);

            // Set references to this game object
            sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
            sfPropertyManager.Get().SetReferences(gameObject, references);

            // Set the parent
            if (obj.Parent != null)
            {
                if (obj.Parent.Type == sfType.Hierarchy)
                {
                    if (gameObject.transform.parent != null)
                    {
                        sfComponentUtils.SetParent(gameObject, null);
                    }
                    if (gameObject.scene != scene)
                    {
                        SceneManager.MoveGameObjectToScene(gameObject, scene);
                    }
                }
                else
                {
                    Transform parent = sfObjectMap.Get().Get<Transform>(obj.Parent);
                    if (parent != null && gameObject.transform.parent != parent)
                    {
                        sfComponentUtils.SetParent(gameObject.transform, parent);
                    }
                }
            }

            // Initialize children
            sfComponentTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfComponentTranslator>(
                sfType.Component);
            sfComponentFinder finder = new sfComponentFinder(gameObject, translator);
            int index = 0;
            foreach (sfObject child in obj.Children)
            {
                if (child.Type == sfType.Component)
                {
                    translator.InitializeComponent(gameObject, child, finder);
                }
                else
                {
                    sfObjectEventDispatcher.Get().OnCreate(child, index);
                }
                index++;
            }
            // Destroy unsynced components
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (!sfObjectMap.Get().Contains(component) && translator.IsSyncable(component))
                {
                    translator.DestroyComponent(component);
                }
            }
            // Sync component order
            if (!finder.InOrder)
            {
                translator.ApplyComponentOrder(gameObject);
            }

            // Unity has a bug where if you instantiate a prefab and set the rotation on the transform to not override
            // the prefab, setting the position and scale through the serialized object won't work and they will have
            // the prefab values. We fix it by setting the position and scale directly on the transform.
            if (isNewPrefab)
            {
                sfObject transformObj = sfObjectMap.Get().GetSFObject(gameObject.transform);
                if (transformObj != null)
                {
                    sfDictionaryProperty transformProperties = (sfDictionaryProperty)transformObj.Property;
                    if (!transformProperties.HasField(sfProp.Rotation))
                    {
                        sfBaseProperty prop;
                        if (transformProperties.TryGetField(sfProp.Position, out prop))
                        {
                            gameObject.transform.localPosition = prop.As<Vector3>();
                        }
                        if (transformProperties.TryGetField(sfProp.Scale, out prop))
                        {
                            gameObject.transform.localScale = prop.As<Vector3>();
                        }
                    }
                }
            }

            if (obj.IsLocked)
            {
                OnLock(obj);
            }
            InvokeOnLockStateChange(obj, gameObject);
            return gameObject;
        }

        /**
         * Gets prefab path and child index data for a game object.
         * 
         * @param   GameObject gameObject to get prefab info for.
         * @param   out string prefabPath, or null if the game object is not a prefab instance.
         * @param   out int childIndex of the object in the prefab, or -1 if the object is the root of the prefab.
         */
        private void GetPrefabInfo(GameObject gameObject, out string prefabPath, out int childIndex)
        {
            GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);
            if (prefab != null)
            {
                prefabPath = AssetDatabase.GetAssetPath(prefab);
                childIndex = prefab.transform.parent == null ? -1 : prefab.transform.GetSiblingIndex();
            }
            else
            {
                sfMissingPrefab missingPrefab = gameObject.GetComponent<sfMissingPrefab>();
                if (missingPrefab != null)
                {
                    prefabPath = missingPrefab.PrefabPath;
                    childIndex = missingPrefab.ChildIndex;
                }
                else
                {
                    prefabPath = null;
                    childIndex = -1;
                }
            }
        }

        /**
         * Gets the prefab path for a game object. If the game object has a sfMissingPrefab component, gets the path
         * from that.
         * 
         * @param   GameObject gameObject to get prefab info for.
         * @return  string prefab path, or null if the game object is not a prefab.
         */
        public string GetPrefabPath(GameObject gameObject)
        {
            GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);
            if (prefab != null)
            {
                return AssetDatabase.GetAssetPath(prefab);
            }
            else
            {
                sfMissingPrefab missingPrefab = gameObject.GetComponent<sfMissingPrefab>();
                if (missingPrefab != null)
                {
                    return missingPrefab.PrefabPath;
                }
            }
            return null;
        }

#if UNITY_2022_2_OR_NEWER
        /**
         * Restores a deleted child from a prefab.
         * 
         * @param   GameObject parent prefab instance with deleted child.
         * @param   int childIndex - index of deleted prefab instance to restore.
         * @return  GameObject resored child prefab, or null if the prefab could not be restored.
         */
        private GameObject RestoreChildPrefab(GameObject parent, int childIndex)
        {
            if (parent == null || childIndex < 0)
            {
                return null;
            }
            GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(parent);
            if (prefab == null)
            {
                return null;
            }
            if (childIndex >= prefab.transform.childCount)
            {
                return null;
            }
            prefab = prefab.transform.GetChild(childIndex).gameObject;
            PrefabUtility.RevertRemovedGameObject(parent, prefab, InteractionMode.AutomatedAction);
            // Unity seems to always add the child at the end, so we start looking at the last child.
            for (int i = parent.transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.transform.GetChild(i).gameObject;
                if (PrefabUtility.GetCorrespondingObjectFromSource(child) == prefab)
                {
                    return child;
                }
            }
            return null;
        }
#endif

        /**
         * Validates that a game object is the correct prefab and is not already in the object map.
         * 
         * @param   GameObject gameObject to validate.
         * @param   string path to prefab the game object should be an instance of.
         * @param   int childIndex the game object's prefab should have. -1 if the game object should not have a prefab
         *          or should be the root of the prefab.
         * @param   sfObject parent sfObject the game object's parent should have if the game object is a prefab child
         *          instance. Not checked if null or if the game object is not a prefab child.
         */
        private bool ValidateGameObject(GameObject gameObject, string path, int childIndex, 
            sfObject parent = null)
        {
            if (sfObjectMap.Get().Contains(gameObject))
            {
                return false;
            }
            string currentPath;
            int currentChildIndex;
            GetPrefabInfo(gameObject, out currentPath, out currentChildIndex);
            if (currentPath != path || currentChildIndex != childIndex)
            {
                return false;
            }
            if (parent != null && childIndex >= 0)
            {
                Transform parentTransform = sfObjectMap.Get().Get<Transform>(parent);
                if (gameObject.transform.parent != parentTransform)
                {
                    return false;
                }
            }
            return true;
        }

        /**
         * Returns the child game object of an sfObject at the given index.
         * 
         * @param   sfObject obj to get child from.
         * @param   int childIndex
         * @return  GameObject gameObject child at the given index, or null if not found.
         */
        private GameObject FindChild(sfObject obj, int childIndex)
        {
            if (obj == null)
            {
                return null;
            }
            Transform parent = sfObjectMap.Get().Get<Transform>(obj);
            if (parent == null)
            {
                return null;
            }
            // If the first child is the lock object, increase the child index by one.
            if (parent.childCount > 0 &&
                (parent.GetChild(0).hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave &&
                parent.GetChild(0).name == sfLockManager.LOCK_OBJECT_NAME)
            {
                childIndex++;
            }
            if (parent.childCount > childIndex)
            {
                return parent.GetChild(childIndex).gameObject;
            }
            return null;
        }

        /**
         * Called when a locally created object is confirmed as created.
         * 
         * @param   sfObject obj that whose creation was confirmed.
         */
        public override void OnConfirmCreate(sfObject obj)
        {
            sfHierarchyWatcher.Get().MarkHierarchyStale();
        }

        /**
         * Called when a game object is deleted by another user.
         * 
         * @param   sfObject obj that was deleted.
         */
        public override void OnDelete(sfObject obj)
        {
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            // Destroy the game object
            if (gameObject != null)
            {
                // Apply server hierarchy changes before deleting the game object in case the game object has children
                // that were reparented.
                ApplyHierarchyChanges();
                sfUI.Get().MarkSceneViewStale();
                sfUI.Get().MarkInspectorStale(gameObject, true);
                DestroyGameObject(gameObject);
                sfHierarchyWatcher.Get().MarkHierarchyStale();
                if (gameObject != null)
                {
                    // Clears the properties and parent/child connections for the object and its descendants, then
                    // reuploads the game object, reusing the sfObjects to preserve ids.
                    OnConfirmDelete(obj, false);
                    return;
                }
            }
            // Remove the game object and its descendants from the guid manager.
            obj.ForSelfAndDescendants((sfObject child) =>
            {
                GameObject go = RemoveMapping(child);
                if (go.IsDestroyed())
                {
                    sfGuidManager.Get().Remove(go);
                }
                return true;
            });
        }

        /**
         * Called when a locally-deleted game object is confirmed as deleted. If unsubscribed is true, removes the
         * object and its descendants from the sfObjectMap. Otherwise clears properties on the object and its
         * descendants, but keeps them in the sfObjectMap so they can be reused if the game object gets recreated so
         * references to the objects will work.
         * 
         * @param   sfObject obj that was confirmed as deleted.
         * @param   bool unsubscribed - true if the deletion occurred because we unsubscribed from the object's parent.
         */
        public override void OnConfirmDelete(sfObject obj, bool unsubscribed)
        {
            if (unsubscribed)
            {
                // Remove the object and its descendants from the sfObjectMap.
                foreach (sfObject child in obj.SelfAndDescendants)
                {
                    sfObjectMap.Get().Remove(child);
                }
            }
            else
            {
                // Clear the properties and children recursively, but keep the objects around so they can be resused to
                // preserve ids if the game object is recreated.
                obj.ForSelfAndDescendants((sfObject child) =>
                {
                    child.Property = new sfDictionaryProperty();
                    if (child.Parent != null)
                    {
                        child.Parent.RemoveChild(child);
                    }
                    GameObject gameObject = sfObjectMap.Get().Get<GameObject>(child);
                    // If the game object still exists, reupload it.
                    if (gameObject != null)
                    {
                        sfHierarchyWatcher.Get().MarkHierarchyStale();
                        AddParentToUploadSet(gameObject);
                    }
                    return true;
                });
            }
        }

        /**
         * Destroys a game object. Logs a warning if the game object could not be destroyed, which occurs if the game
         * object is part of a prefab instance and is not the root of that prefab instance.
         * 
         * @param   GameObject gameObject to destroy.
         */
        public void DestroyGameObject(GameObject gameObject)
        {
            // Remove all notifications for the game object and its descendants.
            sfUnityUtils.ForSelfAndDescendants(gameObject, (GameObject child) =>
            {
                sfNotificationManager.Get().RemoveNotificationsFor(child);
                foreach (Component component in child.GetComponents<Component>())
                {
                    if (component != null)
                    {
                        sfNotificationManager.Get().RemoveNotificationsFor(component);
                    }
                }
                return true;
            });
            EditorUtility.SetDirty(gameObject);
            try
            {
                UObject.DestroyImmediate(gameObject);
            }
            catch (Exception e)
            {
                if (gameObject != null)
                {
                    ksLog.Warning(this, "Unable to destroy game object '" + gameObject.name + "': " + e.Message);
                    // If the object was locked, we want to unlock it.
                    sfUnityUtils.RemoveFlags(gameObject, HideFlags.NotEditable);
                }
                else
                {
                    ksLog.LogException(this, e);
                }
            }
        }

        /**
         * Called when a game object is deleted locally. Deletes the game object on the server, or recreates it if the
         * game object is locked.
         * 
         * @param   int instanceId of game object that was deleted
         */
        private void OnDeleteGameObject(int instanceId)
        {
            // Do not remove the sfObject so it will be reused if the game object is recreated and references to it
            // will be preserved.
            sfObject obj;
            if (!m_instanceIdToSFObjectMap.TryGetValue(instanceId, out obj) || !obj.IsSyncing)
            {
                return;
            }
            // Sync changed hierarchies before deleting the object in case descendants of the object were
            // reparented.
            SyncChangedHierarchies();
            SyncDeletedObject(obj);
        }

        /**
         * Deletes an sfObject for a game object. Recreates the game object if it the sfObject is locked.
         * 
         * @param   sfObject obj to delete.
         */
        private void SyncDeletedObject(sfObject obj)
        {
            if (obj == null)
            {
                return;
            }
            if (obj.Type != sfType.GameObject)
            {
                ksLog.Error(this, "DeleteObject was given a " + obj.Type + " sfObject instead of " +
                    sfType.GameObject + ".");
                return;
            }
            // Remove the notifications for the deleted objects.
            obj.ForSelfAndDescendants((sfObject child) =>
            {
                UObject uobj = sfObjectMap.Get().GetUObject(child);
                if ((object)uobj != null)
                {
                    sfNotificationManager.Get().RemoveNotificationsFor(uobj);
                }
                return true;
            });
            if (obj.IsLocked)
            {
                // The object is locked. Recreate it.
                obj.ForSelfAndDescendants((sfObject child) =>
                {
                    if (child.IsLockPending)
                    {
                        child.ReleaseLock();
                    }
                    RemoveMapping(child);
                    return true;
                });
                OnCreate(obj, obj.Parent == null ? -1 : obj.Parent.Children.IndexOf(obj));
            }
            else
            {
                SceneFusion.Get().Service.Session.Delete(obj);
            }
        }

        /**
         * Called when a game object is created locally. Adds the game object's parent sfObject to set of objects with
         * new children to upload.
         * 
         * @param   GameObject gameObject that was created.
         */
        private void OnCreateGameObject(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                AddParentToUploadSet(gameObject);
            }
        }

        /**
         * Called when a field is removed from a dictionary property.
         * 
         * @param   sfDictionaryProperty dict the field was removed from.
         * @param   string name of the removed field.
         */
        public override void OnRemoveField(sfDictionaryProperty dict, string name)
        {
            base.OnRemoveField(dict, name);
            sfObject obj = dict.GetContainerObject();
            if (!obj.IsLocked)
            {
                return;
            }
            // Gameobjects become unlocked when you set a prefab property to the default value, so we relock it.
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            if (gameObject != null && PrefabUtility.GetPrefabInstanceHandle(gameObject) != null)
            {
                sfUnityUtils.AddFlags(gameObject, HideFlags.NotEditable);
            }
        }

        /**
         * Called when a game object is locked by another user.
         * 
         * @param   sfObject obj that was locked.
         */
        public override void OnLock(sfObject obj)
        {
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            if (gameObject != null)
            {
                Lock(gameObject, obj);
                InvokeOnLockStateChange(obj, gameObject);
            }
        }

        /**
         * Called when a game object is unlocked by another user.
         * 
         * @param   sfObject obj that was unlocked.
         */
        public override void OnUnlock(sfObject obj)
        {
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            if (gameObject != null)
            {
                Unlock(gameObject);
                InvokeOnLockStateChange(obj, gameObject);
            }
        }

        /**
         * Called when a game object's lock owner changes.
         * 
         * @param   sfObject obj whose lock owner changed.
         */
        public override void OnLockOwnerChange(sfObject obj)
        {
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            if (gameObject != null)
            {
                InvokeOnLockStateChange(obj, gameObject);
                sfLockManager.Get().UpdateLockMaterial(gameObject, obj);
            }
        }

        /**
         * Locks a game object.
         * 
         * @param   GameObject gameObject to lock.
         * @param   sfObject obj for the game object.
         */
        private void Lock(GameObject gameObject, sfObject obj)
        {
            sfUnityUtils.AddFlags(gameObject, HideFlags.NotEditable);
            sfUI.Get().MarkInspectorStale(gameObject, true);
            sfLockManager.Get().CreateLockObject(gameObject, obj);
        }

        /**
         * Unlocks a game object.
         * 
         * @param   GameObject gameObject to unlock.
         * @param   bool forceCheckLockObject - if true, will check for a lock object to destroy even when lock shaders
         *          are disabled.
         */
        private void Unlock(GameObject gameObject, bool forceCheckLockObject = false)
        {
            sfUnityUtils.RemoveFlags(gameObject, HideFlags.NotEditable);
            sfUI.Get().MarkInspectorStale(gameObject, true);

            GameObject lockObject = sfLockManager.Get().FindLockObject(gameObject, forceCheckLockObject);
            if (lockObject != null)
            {
                UObject.DestroyImmediate(lockObject);
                sfUI.Get().MarkSceneViewStale();
            }
        }

        /**
         * Called before saving a scene. Temporarily unlocks locked game objects in the scene so they are not saved as
         * not editable.
         * 
         * @param   Scene scene that will be saved.
         */
        private void PreSave(Scene scene)
        {
            foreach (GameObject gameObject in sfUnityUtils.IterateGameObjects(scene))
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
                if (obj != null && obj.IsLocked)
                {
                    sfUnityUtils.RemoveFlags(gameObject, HideFlags.NotEditable);
                    m_tempUnlockedObjects.Add(gameObject);
                }
            }
        }

        /**
         * Relocks all game objects that were temporarily unlocked.
         */
        private void RelockObjects()
        {
            foreach (GameObject gameObject in m_tempUnlockedObjects)
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
                if (obj != null && obj.IsLocked)
                {
                    sfUnityUtils.AddFlags(gameObject, HideFlags.NotEditable);
                }
            }
            m_tempUnlockedObjects.Clear();
        }

        /**
         * Called when a uobject is selected. Syncs the object if it is an unsynced game object.
         * 
         * @param   UObject uobj that was selected.
         */
        private void OnSelect(UObject uobj)
        {
            GameObject gameObject = uobj as GameObject;
            if (gameObject == null)
            {
                return;
            }
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                AddParentToUploadSet(gameObject);
            }
        }

        /**
         * Invokes the OnLockStateChange event.
         * 
         * @param   sfObject obj whose lock state changed.
         * @param   GameObject gameObject whose lock state changed.
         */
        private void InvokeOnLockStateChange(sfObject obj, GameObject gameObject)
        {
            sfHierarchyWatcher.Get().MarkHierarchyStale();
            sfUI.Get().MarkInspectorStale(gameObject);
            if (OnLockStateChange == null)
            {
                return;
            }
            LockType lockType = LockType.UNLOCKED;
            if (obj.IsFullyLocked)
            {
                lockType = LockType.FULLY_LOCKED;
            }
            else if (obj.IsPartiallyLocked)
            {
                lockType = LockType.PARTIALLY_LOCKED;
            }
            OnLockStateChange(gameObject, lockType, obj.LockOwner);
        }

        /**
         * Gets components from a game object. If the game object is a prefab instance with some prefab components
         * removed, the component from the prefab will be in the returned list where the prefab instance component was
         * removed.
         * 
         * @param   GameObject gameObject to get components from.
         * @param   IList<Component> components
         */
        private IList<Component> GetComponents(GameObject gameObject)
        {
            Component[] instanceComponents = gameObject.GetComponents<Component>();
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                return instanceComponents;
            }
            if (PrefabUtility.GetRemovedComponents(gameObject).Count == 0)
            {
                return instanceComponents;
            }
            // The game object is a prefab instance with some prefab components removed. Build a list of components
            // the removed components replaced with the components from the prefab.
            GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);
            Component[] prefabComponents = prefab.GetComponents<Component>();
            List<Component> components = new List<Component>();
            int index = 0;
            Component prefabForInstance = index < instanceComponents.Length ?
                PrefabUtility.GetCorrespondingObjectFromSource(instanceComponents[index]) : null;
            foreach (Component prefabComponent in prefabComponents)
            {
                if (prefabForInstance == prefabComponent)
                {
                    // The component was not removed from the intance. Add the instance component to the list and get
                    // the prefab for the next instance component.
                    components.Add(instanceComponents[index]);
                    index++;
                    prefabForInstance = index < instanceComponents.Length ?
                        PrefabUtility.GetCorrespondingObjectFromSource(instanceComponents[index]) : null;
                }
                else
                {
                    // The component was removed from the instance. Add the prefab component to the list.
                    components.Add(prefabComponent);
                }
            }
            // Add the remaining instance components
            for (int i = index; i < instanceComponents.Length; i++)
            {
                components.Add(instanceComponents[i]);
            }
            return components;
        }

        /**
         * Checks if a game object has a component that will prevent it from syncing.
         * 
         * @param   GameObject gameObject to check for components to prevent syncing.
         * @return  bool true if the gameObject has a component that prevents syncing.
         */
        private bool HasComponentThatPreventsSync(GameObject gameObject)
        {
            if (m_blacklist.Count == 0)
            {
                return false;
            }
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (!CanSyncObjectsWith(component))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
