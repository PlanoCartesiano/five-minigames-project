using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using KS.Reactor;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.SceneFusionCommon;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Manages syncing of components
     */
    public class sfComponentTranslator : sfBaseUObjectTranslator
    {
        /**
         * Callback to initialize a component or sfObject.
         * 
         * @param   sfObject obj
         * @param   Component component
         */
        public delegate void Initializer(sfObject obj, Component component);

        /**
         * Callback for when a component is deleted by another user.
         * 
         * @param   sfObject obj for the component that was deleted.
         * @param   Component component that was deleted.
         */
        public delegate void DeleteHandler(sfObject obj, Component component);

        /**
         * Callback for when a component is deleted locally.
         * 
         * @param   sfObject obj for the component that was deleted locally.
         */
        public delegate void LocalDeleteHandler(sfObject obj);

        /**
         * Maps component types to initializers to call after initializing a component with server values, but
         * before creating the component's children.
         */
        public sfTypeEventMap<Initializer> ComponentInitializers
        {
            get { return m_componentInitializers; }
        }
        private sfTypeEventMap<Initializer> m_componentInitializers = new sfTypeEventMap<Initializer>();

        /**
         * Maps component types to initializers to call after create the sfObject for a component, but before creating
         * the child objects.
         */
        public sfTypeEventMap<Initializer> ObjectInitializers
        {
            get { return m_objectInitializers; }
        }
        private sfTypeEventMap<Initializer> m_objectInitializers = new sfTypeEventMap<Initializer>();

        /**
         * Maps component types to delete handlers to call when components are deleted by the server.
         */
        public sfTypeEventMap<DeleteHandler> DeleteHandlers
        {
            get { return m_deleteHandlers; }
        }
        private sfTypeEventMap<DeleteHandler> m_deleteHandlers = new sfTypeEventMap<DeleteHandler>();

        /**
         * Maps component types to delete handlers to call when components are deleted locally.
         */
        public sfTypeEventMap<LocalDeleteHandler> LocalDeleteHandlers
        {
            get { return m_localDeleteHandlers; }
        }
        private sfTypeEventMap<LocalDeleteHandler> m_localDeleteHandlers = new sfTypeEventMap<LocalDeleteHandler>();

        // Don't sync these component types
        private HashSet<Type> m_blacklist = new HashSet<Type>();
        private HashSet<GameObject> m_componentOrderChangedSet = new HashSet<GameObject>();
        private List<KeyValuePair<sfMissingComponent, Component>> m_replacedComponents = 
            new List<KeyValuePair<sfMissingComponent, Component>>();
        private int m_replacementCount = 0;

        private ksReflectionObject m_roGetCoupledComponent;

        /**
         * Initialization
         */
        public override void Initialize()
        {
            m_roGetCoupledComponent = new ksReflectionObject(typeof(Component)).GetMethod("GetCoupledComponent");
            DontSync<sfMissingPrefab>();

            // This property is not visible in the inspector and is automatically set to the MeshRenderer on the same
            // object so it does not need to be synced. If it is synced and one user hasn't imported TMP and they
            // create a TMP_Text, it will sync as null (it gets set after you import TMP) which causes errors for other
            // users, so we don't sync it.
            sfPropertyManager.Get().Blacklist.Add("TMPro.TMP_Text", "m_renderer");

            // Directly set transform position/rotation/scale to avoid SceneView rendering delays common when using serialized properties.
            m_propertyChangeHandlers.Add<Transform>(sfProp.Position, (UObject uobj, sfBaseProperty prop) =>
            {
                RedrawSceneView(null);
                if (prop == null)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(uobj))
                    {
                        return false;
                    }
                    (uobj as Transform).localPosition = Vector3.zero;
                }
                else
                {
                    (uobj as Transform).localPosition = prop.Cast<Vector3>();
                }
                return true;
            });
            m_propertyChangeHandlers.Add<Transform>(sfProp.Rotation, (UObject uobj, sfBaseProperty prop) =>
            {
                RedrawSceneView(null);
                if (prop == null)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(uobj))
                    {
                        return false;
                    }
                    (uobj as Transform).localRotation = Quaternion.identity;
                }
                else
                {
                    (uobj as Transform).localRotation = prop.Cast<Quaternion>();
                }
                return true;
            });
            m_propertyChangeHandlers.Add<Transform>(sfProp.Scale, (UObject uobj, sfBaseProperty prop) =>
            {
                RedrawSceneView(null);
                if (prop == null)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(uobj))
                    {
                        return false;
                    }
                    (uobj as Transform).localScale = Vector3.one;
                }
                else
                {
                    (uobj as Transform).localScale = prop.Cast<Vector3>();
                }
                return true;
            });

            PostPropertyChange.Add<MeshFilter>("m_Mesh", MarkLockObjectStale);
            PostPropertyChange.Add<SpriteRenderer>("m_Sprite", MarkLockObjectStale);
            PostPropertyChange.Add<LineRenderer>("m_Parameters", MarkLockObjectStale);
            PostPropertyChange.Add<Component>("m_Enabled", (UObject uobj, sfBaseProperty prop) =>
                sfUI.Get().MarkSceneViewStale());

            PostUObjectChange.Add<LODGroup>(MarkLockLODStale);
            PostUObjectChange.Add<Renderer>((UObject uobj) => sfUI.Get().MarkSceneViewStale());
        }

        /**
         * Called after connecting to a session.
         */
        public override void OnSessionConnect()
        {
            SceneFusion.Get().OnUpdate += Update;
            sfUnityEventDispatcher.Get().OnAddOrRemoveComponents += OnAddOrRemoveComponents;
        }

        /**
         * Called after disconnecting from a session.
         */
        public override void OnSessionDisconnect()
        {
            SceneFusion.Get().OnUpdate -= Update;
            sfUnityEventDispatcher.Get().OnAddOrRemoveComponents -= OnAddOrRemoveComponents;
        }

        /**
         * Don't sync components of type T.
         */
        public void DontSync<T>() where T : Component
        {
            m_blacklist.Add(typeof(T));
        }

        /**
         * Don't sync components of the given type.
         * 
         * @param   Type type to not sync.
         */
        public void DontSync(Type type)
        {
            m_blacklist.Add(type);
        }

        /**
         * Checks if a component can be synced. Components are syncable if the following conditions are met:
         *  - the component is not null
         *  - it is not hidden
         *  - it can be saved in the editor
         *  - it is not blacklisted (DontSync wasn't called on its type).
         */
        public bool IsSyncable(Component component)
        {
            return component != null &&
                (component.hideFlags & (HideFlags.HideInInspector | HideFlags.DontSaveInEditor)) == HideFlags.None &&
                !IsBlacklisted(component);
        }

        /**
         * Called every frame.
         * 
         * @param   float deltaTime in seconds since the last update.
         */
        private void Update(float deltaTime)
        {
            // Destroy replaced missing components and update references to the replacement component. Apply properties
            // to the replacement component.
            foreach (KeyValuePair<sfMissingComponent, Component> replacement in m_replacedComponents)
            {
                sfObjectMap.Get().Remove(replacement.Key);
                DestroyComponent(replacement.Key);
                sfObject obj = sfObjectMap.Get().GetSFObject(replacement.Value);
                if (obj != null)
                {
                    sfPropertyManager.Get().ApplyProperties(replacement.Value, (sfDictionaryProperty)obj.Property);
                    sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
                    sfPropertyManager.Get().SetReferences(replacement.Value, references);
                }
            }
            m_replacedComponents.Clear();

            if (m_replacementCount > 0)
            {
                ksLog.Info(this, "Replaced " + m_replacementCount + " missing component(s).");
                m_replacementCount = 0;
            }

            // Apply component order changes from the server
            foreach (GameObject gameObject in m_componentOrderChangedSet)
            {
                ApplyComponentOrder(gameObject);
            }
            m_componentOrderChangedSet.Clear();
        }

        /**
         * Called when components are added or removed from a game object. Checks for new or deleted components on a
         * game object and sends changes to the server, or reverts to the server state if the game object is locked.
         * 
         * @param   GameObject gameObject with added or removed components.
         */
        private void OnAddOrRemoveComponents(GameObject gameObject)
        {
            SyncComponents(gameObject);
        }

        /**
         * Checks for new or deleted components on a game object and sends changes to the server, or reverts to the
         * server state if the game object is locked.
         * 
         * @param   GameObject gameObject to sync components on.
         * @return  bool true if components changed.
         */
        public bool SyncComponents(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null || !obj.IsSyncing)
            {
                return false;
            }
            bool changed = false;
            bool replacedTransform = false;
            sfObject destroyedTransformObj = null;
            if (obj.IsLocked)
            {
                replacedTransform = RestoreDeletedComponents(obj);
            }
            else if (DeleteObjectsForDestroyedComponents(obj, out destroyedTransformObj))
            {
                changed = true;
            }
            sfGameObjectTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfGameObjectTranslator>(
                sfType.GameObject);
            int index = 0;
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (!IsSyncable(component))
                {
                    continue;
                }
                sfObject componentObj = sfObjectMap.Get().GetSFObject(component);
                // Check if the component is new
                if (componentObj == null || !componentObj.IsSyncing)
                {
                    if (obj.IsLocked)
                    {
                        DestroyComponent(component);
                        continue;
                    }
                    if (!translator.CanSyncObjectsWith(component))
                    {
                        EditorUtility.DisplayDialog(Product.NAME + " - Cannot Add Component", "Cannot add '" +
                            component.GetType().Name + "' to a synced game object because objects with that " +
                            "component will not sync.", "OK");
                        DestroyComponent(component);
                        continue;
                    }
                    componentObj = CreateObject(component);
                    if (componentObj != null)
                    {
                        SceneFusion.Get().Service.Session.Create(componentObj, obj, index);
                        changed = true;
                        if (component is Transform && destroyedTransformObj != null)
                        {
                            replacedTransform = true;
                            // Move children from the old transform object to the new transform object.
                            while (destroyedTransformObj.Children.Count > 0)
                            {
                                componentObj.AddChild(destroyedTransformObj.Children[0]);
                            }
                        }
                    }
                }
                index++;
            }

            if (sfUndoManager.Get().IsHandlingUndoRedo)
            {
                // Undoing component addition/removal can change the hideflags, so reset the hideflags.
                if (obj.IsLocked)
                {
                    sfUnityUtils.AddFlags(gameObject, HideFlags.NotEditable);
                }
                else
                {
                    sfUnityUtils.RemoveFlags(gameObject, HideFlags.NotEditable);
                }

                if (replacedTransform)
                {
                    // Undoing relpacement of a Transform with a RectTransorm or vice versa restores the child
                    // state, so we need to sync that.
                    translator.SyncDestroyedChildren(gameObject);
                    translator.SyncHierarchyNextUpdate(sfObjectMap.Get().GetSFObject(gameObject.transform));

                    if (obj.IsLocked)
                    {
                        // If the user undoes a transform replacement on a locked object, then performs a redo, this
                        // can crash Unity, so we clear the redo stack to prevent this.
                        sfUndoManager.Get().ClearRedo();
                    }
                }
            }
            if (destroyedTransformObj != null)
            {
                // Delete the old transform.
                SceneFusion.Get().Service.Session.Delete(destroyedTransformObj);
            }
            return changed;
        }

        /**
         * Applies component order changes from the server to a game object.
         * 
         * @param   GameObject gameObject to apply component order to.
         */
        public void ApplyComponentOrder(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null)
            {
                return;
            }
            // 1: server order, 2: client order
            bool changed = false;
            sfPropertyManager.Get().ApplySerializedProperties(gameObject);
            SerializedObject so = new SerializedObject(gameObject);
            SerializedProperty serializedComponents = so.FindProperty("m_Component");
            int index = 0;
            foreach (sfObject child in obj.Children)
            {
                if (child.Type != sfType.Component)
                {
                    continue;
                }
                Component component1 = sfObjectMap.Get().Get<Component>(child);
                if (component1 == null)
                {
                    continue;
                }
                // Get the next syncable component from the serialized components
                Component component2 = null;
                SerializedProperty sprop = null;
                while (index < serializedComponents.arraySize)
                {
                    sprop = serializedComponents.GetArrayElementAtIndex(index).FindPropertyRelative("component");
                    component2 = sprop.objectReferenceValue as Component;
                    index++;
                    if (component2 == component1 || IsSyncable(component2))
                    {
                        break;
                    }
                }
                // Check if the client component matched the expected component from the server.
                if (component2 != component1 && component2 != null)
                {
                    // Components don't match. Overwrite the client reference.
                    sprop.objectReferenceValue = component1;
                    changed = true;
                }
            }
            if (changed)
            {
                sfPropertyUtils.ApplyProperties(so, true);
            }
        }

        /**
         * Sends component order changes for a game object to the server. If the object is locked, reverts the
         * components to the server order.
         * 
         * @param   GameObject gameObject to sync component order for.
         */
        public void SyncComponentOrder(GameObject gameObject)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
            if (obj == null)
            {
                return;
            }
            if (obj.IsLocked || gameObject.GetComponent<sfMissingPrefab>() != null)
            {
                // Apply the server order at the end of the frame.
                m_componentOrderChangedSet.Add(gameObject);
                return;
            }
            Component[] components = gameObject.GetComponents<Component>();
            sfObject child2 = null;
            // 1: server order, 2: client order
            int index1 = -1;
            int index2 = 0;
            foreach (sfObject child1 in obj.Children)
            {
                index1++;
                if (child1.Type != sfType.Component)
                {
                    continue;
                }
                while (child2 != child1)
                {
                    // Get the next child from the local components
                    child2 = null;
                    while (index2 < components.Length && child2 == null)
                    {
                        child2 = sfObjectMap.Get().GetSFObject(components[index2]);
                        index2++;
                    }
                    if (child2 == null)
                    {
                        return;
                    }
                    // Check if the local component matches the server component
                    if (child2 != child1)
                    {
                        // Components don't match. Move the server component.
                        child2.SetChildIndex(index1);
                        index1++;
                    }
                }
            }
        }

        /**
         * Iterates the component children of an object and recreates destroyed components.
         *
         * @param   sfObject obj to restore deleted component children for.
         * @return  bool true if a destroyed transform was restored.
         */
        private bool RestoreDeletedComponents(sfObject obj)
        {
            int index = -1;
            bool restoredTransform = false;
            foreach (sfObject child in obj.Children)
            {
                index++;
                if (child.Type != sfType.Component)
                {
                    continue;
                }
                sfDictionaryProperty properties = (sfDictionaryProperty)child.Property;
                sfBaseProperty prop;
                if (properties.TryGetField(sfProp.Removed, out prop) && (bool)prop)
                {
                    // This is a prefab component that is removed on the server.
                    continue;
                }
                Component component = sfObjectMap.Get().Get<Component>(child);
                if (component.IsDestroyed())
                {
                    sfObjectMap.Get().Remove(component);
                    if (component is Transform)
                    {
                        GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
                        if (gameObject != null && gameObject.transform.GetType() == component.GetType())
                        {
                            // The transform was replaced with a transform of the same type. Link the old transform's
                            // sfObject to the new transform and apply the server properties.
                            sfObjectMap.Get().Add(child, gameObject.transform);
                            sfPropertyManager.Get().ApplyProperties(gameObject.transform, 
                                (sfDictionaryProperty)child.Property);
                            continue;
                        }
                        restoredTransform = true;
                    }
                    OnCreate(child, index);
                }
            }
            return restoredTransform;
        }

        /**
         * Iterates the component children of an object, looking for destroyed components and deletes their
         * corresponding sfObjects.
         *
         * @param   sfObject obj to check for deleted child components.
         * @param   out sfObject destroyedTransformObj - Set to the sfObject for the destroyed transform if the
         *          transform was destroyed (and replaced with a different type of transform), or set to null.
         * @return  bool true if components were deleted.
         */
        private bool DeleteObjectsForDestroyedComponents(sfObject obj, out sfObject destroyedTransformObj)
        {
            destroyedTransformObj = null;
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj);
            if (gameObject == null)
            {
                return false;
            }
            bool deleted = false;
            foreach (sfObject child in obj.Children)
            {
                if (child.Type != sfType.Component)
                {
                    continue;
                }
                Component component = sfObjectMap.Get().Get<Component>(child);
                if (component.IsDestroyed())
                {
                    deleted = true;
                    LocalDeleteHandler handlers = m_localDeleteHandlers.GetHandlers(component.GetType());
                    if (handlers != null)
                    {
                        handlers(child);
                    }
                    sfNotificationManager.Get().RemoveNotificationsFor(component);
                    sfObjectMap.Get().Remove(child);
                    if (component is Transform)
                    {
                        Transform transform = gameObject.transform;
                        if (transform.GetType() == component.GetType())
                        {
                            // The transform was replaced with another transform of the same type. Link the old
                            // transform's sfObject to the new transform and send property changes.
                            sfObjectMap.Get().Add(child, transform);
                            sfPropertyManager.Get().SendPropertyChanges(transform, (sfDictionaryProperty)child.Property);
                        }
                        else
                        {
                            destroyedTransformObj = child;
                        }
                        continue;
                    }
                    if (PrefabUtility.IsPartOfPrefabInstance(gameObject))
                    {
                        sfDictionaryProperty properties = (sfDictionaryProperty)child.Property;
                        sfBaseProperty prop;
                        if (!properties.TryGetField(sfProp.Added, out prop) || !(bool)prop)
                        {
                            // The removed component is a prefab component. Clear the properties except for the path,
                            // and set removed to true rather than deleting the sfObject.
                            sfDictionaryProperty newProperties = new sfDictionaryProperty();
                            newProperties[sfProp.Class] = properties[sfProp.Class];
                            newProperties[sfProp.Removed] = true;
                            child.Property = newProperties;
                            foreach (sfObject grandChild in child.Children)
                            {
                                SceneFusion.Get().Service.Session.Delete(grandChild);
                            }
                            continue;
                        }
                    }
                    SceneFusion.Get().Service.Session.Delete(child);
                }
            }
            return deleted;
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
            Component component = uobj as Component;
            if (component == null)
            {
                return false;
            }
            if (IsSyncable(component))
            {
                outObj = new sfObject(sfType.Component, new sfDictionaryProperty());
                sfObjectMap.Get().Add(outObj, component);
            }
            return true;
        }

        /**
         * Recursively creates sfObjects for a component and its children.
         * 
         * @param   Component component to create sfObject for.
         * @param   bool isTransform - true if the component is a transform.
         * @param   bool isRemoved - true if this is a prefab component that was removed from the prefab instance.
         */
        public sfObject CreateObject(Component component, bool isTransform = false, bool isRemoved = false)
        {
            sfObject obj = isRemoved ? new sfObject(sfType.Component, new sfDictionaryProperty()) :
                sfObjectMap.Get().GetOrCreateSFObject(component, sfType.Component);
            if (obj.IsSyncing)
            {
                return null;
            }
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            properties[sfProp.Class] = sfComponentUtils.GetName(component);
            if (isRemoved)
            {
                // We set the removed property to true and don't sync other properties for prefab components that were
                // removed from their prefab instance. We still need to create an object for removed prefab components
                // so we know which component was removed if a prefab has the same component twice in a row and one of
                // them was removed.
                properties[sfProp.Removed] = true;
                return obj;
            }
            if (PrefabUtility.IsAddedComponentOverride(component))
            {
                // Added means this is an instance component that was added to a prefab instance. We need to make this
                // distinction in the case where a prefab component is removed from a prefab instance, and then an
                // instance of the same type of component is added.
                properties[sfProp.Added] = true;
            }

            sfMissingComponent missingComponent = component as sfMissingComponent;
            if (missingComponent != null)
            {
                sfMissingScriptSerializer.Get().DeserializeProperties(missingComponent, properties);

                Component reloadedComponent = GetOrAddComponent(missingComponent.gameObject,
                    missingComponent.Name);
                if (reloadedComponent == null)
                {
                    CreateMissingComponentNotification(missingComponent);
                }
                else
                {
                    component = reloadedComponent;
                    m_replacementCount++;
                    sfObjectMap.Get().Add(obj, component);

                    // Reapply the server component order at the end of the frame.
                    m_componentOrderChangedSet.Add(component.gameObject);

                    // Keep the missing component around until the end of the frame to be sure we've created reference
                    // properties for all references to it, then update the references and destroy the component.
                    m_replacedComponents.Add(new KeyValuePair<sfMissingComponent, Component>(missingComponent,
                        component));
                }
            }
            else
            {
                sfPropertyManager.Get().CreateProperties(component, properties);
            }

            // Call the initializers
            Initializer handlers = m_objectInitializers.GetHandlers(component.GetType());
            if (handlers != null)
            {
                handlers(obj, component);
            }

            // Create gameobject child objects if this is a transform
            if (isTransform)
            {
                Transform transform = component as Transform;
                if (transform != null)
                {
                    sfGameObjectTranslator translator = sfObjectEventDispatcher.Get()
                        .GetTranslator<sfGameObjectTranslator>(sfType.GameObject);
                    foreach (Transform childTransform in transform)
                    {
                        if (translator.IsSyncable(childTransform.gameObject))
                        {
                            sfObject child = translator.CreateObject(childTransform.gameObject);
                            if (child != null)
                            {
                                obj.AddChild(child);
                            }
                            else
                            {
                                child = sfObjectMap.Get().GetSFObject(childTransform.gameObject);
                                if (child != null && child.IsSyncing)
                                {
                                    translator.SyncHierarchyNextUpdate(obj);
                                }
                            }
                        }
                    }
                }
            }
            return obj;
        }

        /**
         * Called when a component is created by another user.
         * 
         * @param   sfObject obj that was created.
         * @param   int childIndex of the new object. -1 if the object is a root.
         */
        public override void OnCreate(sfObject obj, int childIndex)
        {
            if (obj.Parent == null || obj.Parent.Type != sfType.GameObject)
            {
                ksLog.Warning(this, "Component object cannot be created without a game object parent.");
                return;
            }
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj.Parent);
            if (gameObject == null)
            {
                return;
            }
            InitializeComponent(gameObject, obj);
            sfUI.Get().MarkSceneViewStale();
            sfUI.Get().MarkInspectorStale(gameObject, true);
            // If this isn't the last compoent, we need to reapply the server component order.
            if (childIndex != obj.Parent.Children.Count - 1)
            {
                m_componentOrderChangedSet.Add(gameObject);
            }
        }

        /**
         * Creates or finds a component for an sfObject and initializes it with server values. Recursively initializes
         * children.
         * 
         * @param   GameObject gameObject to attach the component to.
         * @param   sfObject obj to initialize component for.
         * @param   sfComponentFinder finder for finding the component. If null, will create a new component.
         */
        public void InitializeComponent(GameObject gameObject, sfObject obj, sfComponentFinder finder = null)
        {
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            string name = (string)properties[sfProp.Class];
            sfBaseProperty prop;
            bool isRemoved = properties.TryGetField(sfProp.Removed, out prop) && (bool)prop;
            Component component = finder == null ? null : finder.Find(name);
            sfMissingComponent missingComponent = component as sfMissingComponent;
            sfGameObjectTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfGameObjectTranslator>(
                sfType.GameObject);
            bool isNew = component == null;
            if (isNew)
            {
                if (isRemoved)
                {
                    // This is a prefab component removed from the prefab instance.
                    return;
                }

                // We could not find the component. If the component is a prefab component, try find a removed prefab
                // component for this component we can restore.
                if (PrefabUtility.IsPartOfPrefabInstance(gameObject) &&
                    (!properties.TryGetField(sfProp.Added, out prop) || !(bool)prop))
                {
                    RemovedComponent removedComponent = FindRemovedComponent(obj, gameObject);
                    component = RestoreRemovedComponent(removedComponent);
                    // Restoring a deleted prefab component resets the hideflags, so we need to relock the game
                    // objects in the prefab instance.
                    if (component != null && obj.Parent.IsLocked && translator != null)
                    {
                        translator.RelockPrefabNextPreUpdate(gameObject);
                    }
                }

                // Create the component if we could not find it.
                if (component == null)
                {
                    component = GetOrAddComponent(gameObject, name);
                    if (component == null)
                    {
                        missingComponent = gameObject.AddComponent<sfMissingComponent>();
                        missingComponent.Name = name;
                        component = missingComponent;
                    }
                }

                sfLockManager.Get().MarkLockObjectStale(gameObject);
            }
            else if (isRemoved)
            {
                // We found the component, but the server says this is a prefab component removed from the instance. If
                // the component we found is a prefab component, destroy it.
                if (PrefabUtility.IsPartOfPrefabInstance(component))
                {
                    DestroyComponent(component);
                }
                return;
            }
            else if (missingComponent != null)
            {
                Component reloadedComponent = GetOrAddComponent(gameObject, missingComponent.Name);
                if (reloadedComponent != null)
                {
                    component = reloadedComponent;
                    m_replacementCount++;
                    DestroyComponent(missingComponent);
                    // Reapply the server component order at the end of the frame.
                    m_componentOrderChangedSet.Add(gameObject);
                }
            }

            sfObjectMap.Get().Add(obj, component);

            if (missingComponent != null)
            {
                CreateMissingComponentNotification(missingComponent);
                sfMissingScriptSerializer.Get().SerializeProperties(missingComponent, properties);
            }
            else
            {
                sfPropertyManager.Get().ApplyProperties(component, properties);
            }

            // Set references to this component
            sfReferenceProperty[] references = SceneFusion.Get().Service.Session.GetReferences(obj);
            sfPropertyManager.Get().SetReferences(component, references);

            // Call the component initializers
            Initializer handlers = m_componentInitializers.GetHandlers(component.GetType());
            if (handlers != null)
            {
                handlers(obj, component);
            }

            // Initialize children
            int index = 0;
            foreach (sfObject child in obj.Children)
            {
                if (child.Type == sfType.GameObject && translator != null)
                {
                    if (component != gameObject.transform)
                    {
                        ksLog.Warning(this, "Ignoring game object sfObject with non-transform parent.");
                    }
                    else
                    {
                        GameObject childGameObject = sfObjectMap.Get().Get<GameObject>(child);
                        if (childGameObject == null)
                        {
                            translator.InitializeGameObject(child, gameObject.scene);
                        }
                        else if (childGameObject.transform.parent != gameObject.transform)
                        {
                            sfComponentUtils.SetParent(childGameObject, gameObject);
                        }
                    }
                }
                else
                {
                    sfObjectEventDispatcher.Get().OnCreate(child, index);
                }
                index++;
            }
            if (component == gameObject.transform && translator != null)
            {
                // Destroy unsynced children
                for (int i = gameObject.transform.childCount - 1; i >= 0; i--)
                {
                    GameObject child = gameObject.transform.GetChild(i).gameObject;
                    if (!sfObjectMap.Get().Contains(child) && translator.IsSyncable(child))
                    {
                        translator.DestroyGameObject(child);
                    }
                }
                // Sync child order
                if (obj.Children.Count > 0)
                {
                    translator.ApplyHierarchyChanges(obj);
                }
            }
        }

        /**
         * Gets a component by name that is not mapped to an sfObject, or creates one if none is found. You can get the
         * name using sfComponentUtils.GetName. We check for an existing component instead of always adding one because
         * sometimes adding one component will cause another component to be added, and if we then added the second
         * component it would be added twice.
         * 
         * @param   GameObject gameObject to get component from or add component to.
         * @return  string name of component to get or add.
         */
        public Component GetOrAddComponent(GameObject gameObject, string name)
        {
            Component[] components = gameObject.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (sfComponentUtils.GetName(component) == name && component.GetType() != typeof(sfMissingComponent) &&
                    !sfObjectMap.Get().Contains(component))
                {
                    // If this isn't the last compoent, we need to reapply the server component order.
                    if (i != components.Length - 1)
                    {
                        m_componentOrderChangedSet.Add(gameObject);
                    }
                    return component;
                }
            }
            // Apply pending serialized property modifications to the game object before adding a component to prevent
            // the game object's component list being reverted and losing the new component when property modifications
            // are applied.
            sfPropertyManager.Get().ApplySerializedProperties(gameObject);
            return sfComponentUtils.AddComponent(gameObject, name);
        }

        /**
         * Finds a removed prefab component on a prefab instance for an sfObject, if one exists.
         * 
         * @param   sfObject obj to get removed component for.
         * @param   GameObject gameObject to get the removed component from.
         */
        private RemovedComponent FindRemovedComponent(sfObject obj, GameObject gameObject)
        {
            List<RemovedComponent> removedComponents = PrefabUtility.GetRemovedComponents(gameObject);
            if (removedComponents.Count == 0)
            {
                return null;
            }
            // Remove all compnents from the list whose type does not match what we are looking for.
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            string name = (string)properties[sfProp.Class];
            for (int i = removedComponents.Count - 1; i >= 0; i--)
            {
                if (sfComponentUtils.GetName(removedComponents[i].assetComponent) != name)
                {
                    removedComponents.RemoveAt(i);
                }
            }
            if (removedComponents.Count == 0)
            {
                // We didn't find any removed components of the type we were looking for.
                return null;
            }
            if (removedComponents.Count == 1)
            {
                // We found only one removed omponent of the type we were looking for. Return it.
                return removedComponents[0];
            }
            int index = 0;
            sfBaseProperty prop;
            // Count the number of sibling sfObjects for removed components of the same type prior to the sfObject we
            // are looking for, then return the removed component at that index.
            foreach (sfObject sibling in obj.Parent.Children)
            {
                if (sibling == obj)
                {
                    break;
                }
                properties = (sfDictionaryProperty)sibling.Property;
                if (name == (string)properties[sfProp.Class] &&
                    properties.TryGetField(sfProp.Removed, out prop) && (bool)prop)
                {
                    index++;
                    if (index == removedComponents.Count - 1)
                    {
                        break;
                    }
                }
            }
            return removedComponents[index];
        }

        /**
         * Restores a removed prefab component to a prefab instance.
         * 
         * @param   RemovedComponent removedComponent to restore.
         */
        private Component RestoreRemovedComponent(RemovedComponent removedComponent)
        {
            if (removedComponent == null)
            {
                return null;
            }
            // Restore the component. This code is taken from the decompiled Unity code for RemovedComponent.Revert(),
            // except the InteractionMode is changed to prevent registering an operation on the undo stack.
            PrefabUtility.RevertRemovedComponent(removedComponent.containingInstanceGameObject,
                removedComponent.assetComponent, InteractionMode.AutomatedAction);
            // Not sure what this does but it's in Unity's code...
            Component coupledComponent = m_roGetCoupledComponent.InstanceInvoke(
                removedComponent.assetComponent) as Component;
            if (coupledComponent != null || coupledComponent.IsDestroyed())
            {
                PrefabUtility.RevertRemovedComponent(removedComponent.containingInstanceGameObject, coupledComponent,
                    InteractionMode.AutomatedAction);
            }

            // Find the instance component we restored
            foreach (Component component in removedComponent.containingInstanceGameObject.GetComponents<Component>())
            {
                if (PrefabUtility.GetCorrespondingObjectFromSource(component) == removedComponent.assetComponent)
                {
                    return component;
                }
            }
            return null;
        }

        /**
         * Creates the notification for a missing component.
         * 
         * @param   sfMissingComponent missingComponent to create notification for.
         */
        private void CreateMissingComponentNotification(sfMissingComponent missingComponent)
        {
            sfNotification.Create(sfNotificationCategory.MissingComponent, "Unable to load component '" +
                sfComponentUtils.GetDisplayName(missingComponent.Name) + "'.", missingComponent);
        }

        /**
         * Called when a component is deleted by another user.
         * 
         * @param   sfObject obj that was deleted.
         */
        public override void OnDelete(sfObject obj)
        {
            Component component = sfObjectMap.Get().Remove(obj) as Component;
            if (component != null)
            {
                DeleteHandler handlers = m_deleteHandlers.GetHandlers(component.GetType());
                if (handlers != null)
                {
                    handlers(obj, component);
                }
                
                MarkLockObjectStale(component);
                sfUI.Get().MarkSceneViewStale();
                sfUI.Get().MarkInspectorStale(component);
                sfNotificationManager.Get().RemoveNotificationsFor(component);
                DestroyComponent(component);
            }
        }

        /**
         * Called when a locally-deleted component is confirmed as deleted.
         * 
         * @param   sfObject obj that was confirmed as deleted.
         * @param   bool unsubscribed - true if the deletion occurred because we unsubscribed from the object's parent.
         */
        public override void OnConfirmDelete(sfObject obj, bool unsubscribed)
        {
            // Clear the properties and children, but keep the object around so it can be resused to preserve ids if
            // the component is recreated.
            obj.Property = new sfDictionaryProperty();
            while (obj.Children.Count > 0)
            {
                obj.RemoveChild(obj.Children[0]);
            }
        }

        /**
         * Called when a component's parent or child index is changed by another user.
         * 
         * @param   sfObject obj whose parent changed.
         * @param   int childIndex of the object. -1 if the object is a root.
         */
        public override void OnParentChange(sfObject obj, int childIndex)
        {
            GameObject gameObject = sfObjectMap.Get().Get<GameObject>(obj.Parent);
            if (gameObject != null)
            {
                m_componentOrderChangedSet.Add(gameObject);
            }
        }

        /**
         * Destroys a component. First destroys any components that depend on the component being destroyed.
         * 
         * @param   Component component to destroy.
         */
        public void DestroyComponent(Component component)
        {
            if (component is Transform)
            {
                // Transform components cannot be removed, however they can be replaced by adding a RectTransform,
                // and RectTransforms can be replaced by adding a Transform. The component will be destroyed when the
                // replacement Transform is added...
                return;
            }
            // Apply pending serialized property modifications before destroying the component to prevent the component
            // being readded and causing a component cannot be loaded warning when serialized properties are applied.
            sfPropertyManager.Get().ApplySerializedProperties(component.gameObject);
            RemoveDependentComponents(component);
            EditorUtility.SetDirty(component);
            UObject.DestroyImmediate(component);
        }

        /**
         * Removes all components on a game object that depend on the given component recursively, so if A depends on B
         * depends on C and we call this with A, first C is removed and then B.
         * 
         * @param   Component component to remove dependent components for.
         * @param   Stack<Component> previousComponents already being removed. Contains one component for each level of
         *          recursion. Used to detect circular dependencies.
         */
        private void RemoveDependentComponents(Component component, Stack<Component> previousComponents = null)
        {
            List<Component> dependents = new List<Component>();
            Type type = component.GetType();
            foreach (Component comp in component.GetComponents<Component>())
            {
                if (comp == component || comp == null)
                {
                    continue;
                }
                Type currentType = comp.GetType();
                if (currentType == type)
                {
                    // There's another component of the same type so we can delete this one without breaking any
                    // dependencies
                    return;
                }
                Type requiredType;
                if (sfDependencyTracker.Get().DependsOn(currentType, type, out requiredType) && 
                    (requiredType == type ||
                    // make sure there isn't another component that shares the same required base class
                    component.GetComponents(requiredType).Length <= 1))
                {
                    dependents.Add(comp);
                }
            }
            if (dependents.Count <= 0)
            {
                return;
            }
            if (previousComponents == null)
            {
                previousComponents = new Stack<Component>();
            }
            previousComponents.Push(component);
            foreach (Component comp in dependents)
            {
                if (previousComponents.Contains(comp))
                {
                    ksLog.Error(this, "Detected circular dependency while trying to remove components.");
                }
                else
                {
                    RemoveDependentComponents(comp, previousComponents);
                    EditorUtility.SetDirty(comp);
                    UObject.DestroyImmediate(comp);
                }
            }
            previousComponents.Pop();
        }

        /**
         * Called when an sfObject property changes.
         * 
         * @param   sfBaseProperty property that changed.
         */
        public override void OnPropertyChange(sfBaseProperty property)
        {
            // If it was not the root property that changed, call the base function.
            if (property.GetDepth() > 0)
            {
                base.OnPropertyChange(property);
                return;
            }
            sfObject obj = property.GetContainerObject();
            sfDictionaryProperty properties = (sfDictionaryProperty)property;
            sfBaseProperty prop;
            // If the removed property is true, this is a prefab component removed from its prefab instance. Delete the
            // component.
            if (properties.TryGetField(sfProp.Removed, out prop) && (bool)prop)
            {
                OnDelete(obj);
            }
            else if (obj.Parent != null)
            {
                // This was a removed prefab component that got added back to its prefab instance. Recreate it.
                int index = 0;
                foreach (sfObject sibling in obj.Parent.Children)
                {
                    if (sibling == obj)
                    {
                        break;
                    }
                    index++;
                }
                OnCreate(obj, index);
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
            sfObject obj = dict.GetContainerObject().Parent;
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
         * Redraws the scene view.
         * 
         * @param UObject uobj. Does nothing.
         */
        private void RedrawSceneView(UObject uobj)
        {
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Marks the given UObject's lock object stale.
         * 
         * @param   UObject uobj
         * @param   sfBaseProperty property - unused. This parameter is here so the function can be used as a post
         *          property change handler.
         */
        private void MarkLockObjectStale(UObject uobj, sfBaseProperty property = null)
        {
            sfLockManager.Get().MarkLockObjectStale(((Component)uobj).gameObject);
        }

        /**
         * Marks the given LOD group's lock LODs stale.
         * 
         * @param   UObject lodGroup
         */
        private void MarkLockLODStale(UObject lodGroup)
        {
            sfLockManager.Get().MarkLockLODStale(((LODGroup)lodGroup).gameObject);
        }

        /**
         * Checks if a component is black listed.
         * 
         * @param   Component component to check.
         * @return  bool true if the component is black listed.
         */
        private bool IsBlacklisted(Component component)
        {
            if (m_blacklist.Count == 0)
            {
                return false;
            }
            foreach (Type type in m_blacklist)
            {
                if (type.IsAssignableFrom(component.GetType()))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
