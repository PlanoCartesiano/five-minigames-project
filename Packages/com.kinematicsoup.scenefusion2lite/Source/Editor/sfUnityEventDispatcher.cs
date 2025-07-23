using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using KS.SceneFusion;
using KS.SceneFusion2.Client;
using KS.Reactor;
using UObject = UnityEngine.Object;

#if !UNITY_2021_3_OR_NEWER
using UnityEngine.Experimental.TerrainAPI;
#endif

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Listens for and dispatches Unity events, in some cases changing the parameters of the event. Allows all events
     * to be enabled or disabled. Register with this class instead of directly against the Unity events to ensure you
     * do not respond to events that were triggered by Scene Fusion.
     */
    public class sfUnityEventDispatcher
    {
        /**
         * @return  sfUnityEventDispatcher singleton instance
         */
        public static sfUnityEventDispatcher Get()
        {
            return m_instance;
        }
        private static sfUnityEventDispatcher m_instance = new sfUnityEventDispatcher();

        /**
         * Are events enabled?
         */
        public bool Enabled
        {
            get { return m_enabled; }
        }
        private bool m_enabled = false;

        /**
         * Invoked when a scene is opened or a new scene is created.
         */
        public event EditorSceneManager.SceneOpenedCallback OnOpenScene;

        /**
         * Invoked when a scene is closed.
         */
        public event EditorSceneManager.SceneClosingCallback OnCloseScene;

        /**
         * Create game object event callback.
         * 
         * @param   GameObject gameObject that was created.
         */
        public delegate void CreateCallback(GameObject gameObject);

        /**
         * Invoked when a game object is created. Only invoked if an undo operation is registered for the object
         * creation.
         */
        public event CreateCallback OnCreate;

        /**
         * Delete game object event callback.
         * 
         * @param   int instanceId of game object that was deleted.
         */
        public delegate void DeleteCallback(int instanceId);

        /**
         * Invoked when a game object is deleted.
         */
        public event DeleteCallback OnDelete;

        /**
         * Hierarchy structure change event callback.
         * 
         * @param   GameObject gameObject whose hierarchy structure changed. This object and and of its descendants may
         *          have changed.
         */
        public delegate void HierarchyStructureChangeCallback(GameObject gameObject);

        /**
         * Invoked when an action is performed that changes a game object and possibly any of its descendants, and we
         * don't know what specifically changed. Examples of such actions are unpacking or reverting a prefab instance,
         * or any changes made after calling Undo.RegisterFullObjectHierarchyUndo.
         */
        public event HierarchyStructureChangeCallback OnHierarchyStructureChange;

        /**
         * Properties changed event callback.
         * 
         * @param   UObject uobj whose properties changed.
         */
        public delegate void PropertiesChangedCallback(UObject uobj);

        /**
         * Invoked when properties on a uobject changed, but we don't know which properties.
         */
        public event PropertiesChangedCallback OnPropertiesChanged;

        /**
         * Add or remove components event callback.
         * 
         * @param   GameObject gameObject with added and/or removed components.
         */
        public delegate void AddOrRemoveComponentsCallback(GameObject gameObject);

        /**
         * Invoked when components are added to or removed from a game object.
         */
        public event AddOrRemoveComponentsCallback OnAddOrRemoveComponents;

        /**
         * Parent change event callback.
         * 
         * @param   GameObject gameObject whose parent changed.
         */
        public delegate void ParentChangeCallback(GameObject gameObject);

        /**
         * Invoked when a game object's parent changes.
         */
        public event ParentChangeCallback OnParentChange;

        /**
         * Reorder children event callback.
         * 
         * @param   GameObject gameObject whose children were reordered.
         */
        public delegate void ReorderChildrenCallback(GameObject gameObject);

        /**
         * Invoked when a game object's children are reordered.
         */
        public event ReorderChildrenCallback OnReorderChldren;

        /**
         * New prefab event handler.
         * 
         * @param   string path to new prefab.
         */
        public delegate void NewPrefabHandler(string path);

        /**
         * Scene change event handler.
         * 
         * @param   GameObject gameObject that was moved to a different scene.
         */
        public delegate void SceneChangeHandler(GameObject gameObject);

        /**
         * Invoked when a terrain's heightmap is changed.
         */
        public event TerrainCallbacks.HeightmapChangedCallback OnTerrainHeightmapChange;

        /**
         * Invoked when a terrain's textures are changed
         */
        public event TerrainCallbacks.TextureChangedCallback OnTerrainTextureChange;

        public delegate void TerrainDetailChangedCallback(TerrainData terrainData, RectInt changeArea, int layer);
        public event TerrainDetailChangedCallback OnTerrainDetailChange;

        public delegate void TerrainTreeChangedCallback(TerrainData terrainData, bool hasRemovals);
        public event TerrainTreeChangedCallback OnTerrainTreeChange;

        public delegate void TerrainCheckCallback(TerrainData terrainData);
        public event TerrainCheckCallback OnTerrainCheck;

        /**
         * Invoked when properties are modified.
         */
        public event Undo.PostprocessModifications OnModifyProperties
        {
            add
            {
                if (value != null)
                {
                    m_propertyModificationHandlers.Add(value);
                }
            }
            remove
            {
                if (value != null)
                {
                    m_propertyModificationHandlers.Remove(value);
                }
            }
        }
        private List<Undo.PostprocessModifications> m_propertyModificationHandlers =
            new List<Undo.PostprocessModifications>();

        /**
         * Types of events that can be invoked from InvokeChangeEvents.
         */
        [Flags]
        private enum Events
        {
            NONE = 0,
            CREATE = 1 << 0,
            // DELETE isn't here because we cannot get the UObject for delete events and we need a UObject to use these
            // flags.
            ADD_REMOVE_COMPONENT = 1 << 1,
            CHANGE_PROPERTIES = 1 << 2,
            CHANGE_HIERARCHY_STRUCTURE = 1 << 3,
            CHANGE_PARENT = 1 << 4,
            REORDER_CHILDREN = 1 << 5
        }

        // Tracks all distinct events called upon a specific UObject during an InvokeChangesEvents call. Used to
        // prevent double invoking events.
        private Dictionary<UObject, Events> m_invokedEventMap = 
            new Dictionary<UObject, Events>();

        /**
         * Singleton constructor
         */
        private sfUnityEventDispatcher()
        {
            
        }

        /**
         * Enables events. Starts listening for Unity events.
         */
        public void Enable()
        {
            if (m_enabled)
            {
                return;
            }
            m_enabled = true;
            EditorSceneManager.newSceneCreated += InvokeOnOpenScene;
            EditorSceneManager.sceneOpened += InvokeOnOpenScene;
            EditorSceneManager.sceneClosing += InvokeOnCloseScene;
            Undo.postprocessModifications += InvokeOnModifyProperties;
            TerrainCallbacks.heightmapChanged += InvokeOnHeightmapChange;
            TerrainCallbacks.textureChanged += InvokeOnTextureChange;
        }

        /**
         * Disables events. Stops listening for Unity events.
         */
        public void Disable()
        {
            if (!m_enabled)
            {
                return;
            }
            m_enabled = false;
            EditorSceneManager.newSceneCreated -= InvokeOnOpenScene;
            EditorSceneManager.sceneOpened -= InvokeOnOpenScene;
            EditorSceneManager.sceneClosing -= InvokeOnCloseScene;
            Undo.postprocessModifications -= InvokeOnModifyProperties;
            TerrainCallbacks.heightmapChanged -= InvokeOnHeightmapChange;
            TerrainCallbacks.textureChanged -= InvokeOnTextureChange;
        }

        /**
         * If the dispatcher is already enabled, calls the callback. Otherwise enables the dispatcher before calling
         * the callback and disables it again afterwards.
         * 
         * @param   Action callback to call with the dispatcher enabled.
         */
        public void TempEnable(Action callback)
        {
            if (m_enabled)
            {
                callback();
                return;
            }
            Enable();
            try
            {
                callback();
            }
            finally
            {
                Disable();
            }
        }

        /**
         * Invokes the on open scene event.
         * 
         * @param   Scene scene that was created.
         * @param   NewSceneSetup setup
         * @param   NewSceneMode mode the scene was created with.
         */
        private void InvokeOnOpenScene(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            InvokeOnOpenScene(scene, mode == NewSceneMode.Additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
        }

        /**
         * Invokes the on open scene event.
         * 
         * @param   Scene scene that was opened.
         * @param   OpenSceneMode mode the scene was opened with.
         */
        public void InvokeOnOpenScene(Scene scene, OpenSceneMode mode)
        {
            if (OnOpenScene != null)
            {
                OnOpenScene(scene, mode);
            }
        }

        /**
         * Invokes the on close scene event.
         * 
         * @param   Scene scene that was closed.
         * @param   bool removed - true if the scene was removed.
         */
        public void InvokeOnCloseScene(Scene scene, bool removed)
        {
            if (OnCloseScene != null)
            {
                OnCloseScene(scene, removed);
            }
        }

        /**
         * Invokes the on modify properties event.
         * 
         * @param   UndoPropertyModification[] modifications. Remove modifications from the returned array to prevent
         *          them.
         * @return  UndoPropertyModification[] modifications that are allowed.
         */
        public UndoPropertyModification[] InvokeOnModifyProperties(UndoPropertyModification[] modifications)
        {
            foreach (Undo.PostprocessModifications handler in m_propertyModificationHandlers)
            {
                modifications = handler(modifications);
            }
            return modifications;
        }

        /**
         * Called when operations were recorded on the undo stack. Invokes events for changes.
         * 
         * @param   ref ObjectChangeEventStream stream of change events.
         */
        public void InvokeChangeEvents(ref ObjectChangeEventStream stream)
        {
            if (!m_enabled)
            {
                return;
            }
            for (int i = 0; i < stream.length; i++)
            {
                try
                {
                    InvokeEvent(ref stream, i);
                }
                catch (Exception e)
                {
                    ksLog.Error("Error handling " + stream.GetEventType(i) + " event.", e);
                }
            }
            m_invokedEventMap.Clear();
        }

        /**
         * Invokes an event for the change event at the given index in the stream if an event can be invoked for it.
         * The types of events that can be invoked are:
         *  - Game object creation
         *  - Game object deletion
         *  - Change game object structure hierarchy
         *  - Change properties (invoked when we don't know which properties changed)
         *  - Add and/or remove components
         *  - Change game object parent
         *  - Reorder game object children
         *  
         *  @param  ref ObjectChangeEventStream stream of change events.
         *  @param  int index of event in stream to invoke event for.
         */
        private void InvokeEvent(ref ObjectChangeEventStream stream, int index)
        {
            switch (stream.GetEventType(index))
            {
                case ObjectChangeKind.CreateGameObjectHierarchy:
                    InvokeCreateEvent(ref stream, index); break;
                case ObjectChangeKind.DestroyGameObjectHierarchy:
                    InvokeDeleteEvent(ref stream, index); break;
                case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                    InvokeHierarchyStructureChangeEvent(ref stream, index); break;
                case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    InvokePropertiesChangedEvent(ref stream, index); break;
                case ObjectChangeKind.ChangeAssetObjectProperties:
                    InvokePropertiesChangedEventForAsset(ref stream, index); break;
                case ObjectChangeKind.ChangeGameObjectStructure: 
                    InvokeAddOrRemoveComponentsEvent(ref stream, index); break;
                case ObjectChangeKind.ChangeGameObjectParent:
                    InvokeParentChangeEvent(ref stream, index); break;
#if UNITY_2022_2_OR_NEWER
                case ObjectChangeKind.ChangeChildrenOrder:
                    InvokeReorderChildrenEvent(ref stream, index); break;
#endif
                case ObjectChangeKind.UpdatePrefabInstances:
                    InvokeHierarchyStructureChangeEventsForPrefabs(ref stream, index); break;
            }
        }

        /**
         * Invokes the on create event.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokeCreateEvent(ref ObjectChangeEventStream stream, int index)
        {
            if (OnCreate == null)
            {
                return;
            }
            CreateGameObjectHierarchyEventArgs data;
            stream.GetCreateGameObjectHierarchyEvent(index, out data);
            GameObject gameObj = EditorUtility.InstanceIDToObject(data.instanceId) as GameObject;
            if (gameObj != null)
            {
                SetEventFlag(gameObj, Events.CREATE);
                OnCreate(gameObj);
            }
        }

        /**
         * Invokes the on delete event.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokeDeleteEvent(ref ObjectChangeEventStream stream, int index)
        {
            if (OnDelete == null)
            {
                return;
            }
            DestroyGameObjectHierarchyEventArgs data;
            stream.GetDestroyGameObjectHierarchyEvent(index, out data);
            OnDelete(data.instanceId);
        }

        /**
         * Invokes the on hierarchy structure change event if it hasn't already been invoked for the game object.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokeHierarchyStructureChangeEvent(ref ObjectChangeEventStream stream, int index)
        {
            if (OnHierarchyStructureChange == null)
            {
                return;
            }
            ChangeGameObjectStructureHierarchyEventArgs data;
            stream.GetChangeGameObjectStructureHierarchyEvent(index, out data);
            GameObject gameObj = EditorUtility.InstanceIDToObject(data.instanceId) as GameObject;
            if (gameObj != null && SetEventFlag(gameObj, Events.CHANGE_HIERARCHY_STRUCTURE))
            {
                OnHierarchyStructureChange(gameObj);
            }
        }

        /**
         * Invokes the on hierarchy structure change event for each updated prefab instance if it hasn't already been
         * invoked for that object.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke events for.
         */
        private void InvokeHierarchyStructureChangeEventsForPrefabs(ref ObjectChangeEventStream stream, int index)
        {
            if (OnHierarchyStructureChange == null)
            {
                return;
            }
            UpdatePrefabInstancesEventArgs data;
            stream.GetUpdatePrefabInstancesEvent(index, out data);
            foreach (int id in data.instanceIds)
            {
                GameObject gameObj = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (gameObj != null && SetEventFlag(gameObj, Events.CHANGE_HIERARCHY_STRUCTURE))
                {
                    OnHierarchyStructureChange(gameObj);
                }
            }
        }

        /**
         * Invokes the on parent change event if it hasn't already been invoked for the game object.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokeParentChangeEvent(ref ObjectChangeEventStream stream, int index)
        {
            if (OnParentChange == null)
            {
                return;
            }
            ChangeGameObjectParentEventArgs data;
            stream.GetChangeGameObjectParentEvent(index, out data);
            GameObject gameObj = EditorUtility.InstanceIDToObject(data.instanceId) as GameObject;
            if (gameObj != null && SetEventFlag(gameObj, Events.CHANGE_PARENT))
            {
                OnParentChange(gameObj);
            }
        }

#if UNITY_2022_2_OR_NEWER
        /**
         * Invokes the on reorder children event if it hasn't already been invoked for the game object.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokeReorderChildrenEvent(ref ObjectChangeEventStream stream, int index)
        {
            if (OnReorderChldren == null)
            {
                return;
            }
            ChangeChildrenOrderEventArgs data;
            stream.GetChangeChildrenOrderEvent(index, out data);
            GameObject gameObj = EditorUtility.InstanceIDToObject(data.instanceId) as GameObject;
            if (gameObj != null && SetEventFlag(gameObj, Events.REORDER_CHILDREN))
            {
                OnReorderChldren(gameObj);
            }
        }
#endif

        /**
         * Invokes the on reorder children event for a game object.
         * 
         * @param   GameObject parent to invoke the event for.
         */
        public void InvokeOnReorderChildren(GameObject gameObj)
        {
            if (OnReorderChldren != null && gameObj != null)
            {
                OnReorderChldren(gameObj);
            }
        }

        /**
         * Invokes the on properties change event if we don't know which properties changed and it hasn't already been
         * invoked for the game object. Usually when properties change, Unity fires other events to tell us which
         * properties changed, however in some cases such as when reverting prefab instance overrides or when using
         * Undo.RegsiterCompleteObjectUndo, Unity does not tell us what changed so we invoke this event.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokePropertiesChangedEvent(ref ObjectChangeEventStream stream, int index)
        {
            if (OnPropertiesChanged == null || sfUndoManager.Get().HasPendingPropertyModifications)
            {
                return;
            }
            ChangeGameObjectOrComponentPropertiesEventArgs data;
            stream.GetChangeGameObjectOrComponentPropertiesEvent(index, out data);
            UObject uobj = EditorUtility.InstanceIDToObject(data.instanceId);
            if (uobj != null && SetEventFlag(uobj, Events.CHANGE_PROPERTIES))
            {
                OnPropertiesChanged(uobj);
            }
        }

        /**
         * Invokes the on properties change event for an asset if we don't know which properties changed and it hasn't
         * already been invoked for the asset. Usually when properties change, Unity fires other events to tell us
         * which properties changed, however in some cases such as when editing material shader propertis or using 
         * Undo.RegsiterCompleteObjectUndo, Unity does not tell us what changed so we invoke this event.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokePropertiesChangedEventForAsset(ref ObjectChangeEventStream stream, int index)
        {
            if (OnPropertiesChanged == null || sfUndoManager.Get().HasPendingPropertyModifications)
            {
                return;
            }
            ChangeAssetObjectPropertiesEventArgs data;
            stream.GetChangeAssetObjectPropertiesEvent(index, out data);
            UObject uobj = EditorUtility.InstanceIDToObject(data.instanceId);
            if (uobj != null && SetEventFlag(uobj, Events.CHANGE_PROPERTIES))
            {
                OnPropertiesChanged(uobj);
            }
        }

        /**
         * Invokes the on add or remove components event if it or a create event hasn't already been invoked for the
         * game object.
         * 
         * @param  ref ObjectChangeEventStream stream of change events.
         * @param  int index of event in stream to invoke event for.
         */
        private void InvokeAddOrRemoveComponentsEvent(ref ObjectChangeEventStream stream, int index)
        {
            if (OnAddOrRemoveComponents == null)
            {
                return;
            }
            ChangeGameObjectStructureEventArgs data;
            stream.GetChangeGameObjectStructureEvent(index, out data);
            GameObject gameObject = EditorUtility.InstanceIDToObject(data.instanceId) as GameObject;
            if (gameObject != null && !HasEvent(gameObject, Events.CREATE | Events.ADD_REMOVE_COMPONENT))
            {
                SetEventFlag(gameObject, Events.ADD_REMOVE_COMPONENT);
                OnAddOrRemoveComponents(gameObject);
            }
        }

        /**
         * Checks if any of the given events were invoked for a uobject.
         * 
         * @param   UObject uobj
         * @param   Events events to check for.
         * @return  bool true if any of the events were invoked for the uobject.
         */
        private bool HasEvent(UObject uobj, Events events)
        {
            Events flags;
            return m_invokedEventMap.TryGetValue(uobj, out flags) && (flags & events) != Events.NONE;
        }

        /**
         * Sets an event flag in the invoked event map for a uobject.
         * 
         * @param   UObject uobj
         * @param   Events flag to set.
         * @return  bool false if the flag was already set.
         */
        private bool SetEventFlag(UObject uobj, Events flag)
        {
            Events events;
            if (m_invokedEventMap.TryGetValue(uobj, out events) && (events & flag) == flag)
            {
                return false;
            }
            m_invokedEventMap[uobj] = events | flag;
            return true;
        }

        /**
         * Invokes the on heightmap change event. This fires when the terrain heightmap changed.
         * 
         * @param   Terrain terrain - the Terrain object that references a changed TerrainData asset.
         * @param   RectInt changeArea - the area that the heightmap changed.
         * @param   bool synced - indicates whether the changes were fully synchronized back to CPU memory.
         */
        public void InvokeOnHeightmapChange(Terrain terrain, RectInt changeArea, bool synced)
        {
            if (OnTerrainHeightmapChange != null)
            {
                OnTerrainHeightmapChange(terrain, changeArea, synced);
            }
        }

        /**
         * Invokes the on texture change event. This fires when the terrain textures changed.
         * 
         * @param   Terrain terrain - the Terrain object that references a changed TerrainData asset.
         * @param   string textureName - the name of the texture that changed.
         * @param   RectInt changeArea - the region of the Terrain texture that changed, in texel coordinates.
         * @param   bool synced - indicates whether the changes were fully synchronized back to CPU memory.
         */
        public void InvokeOnTextureChange(Terrain terrain, string textureName, RectInt changeArea, bool synced)
        {
            if (OnTerrainTextureChange != null)
            {
                OnTerrainTextureChange(terrain, textureName, changeArea, synced);
            }
        }

        public void InvokeOnTerrainDetailChange(Terrain terrain, RectInt changeArea, int layer)
        {
            if (OnTerrainDetailChange != null && terrain != null)
            {
                OnTerrainDetailChange(terrain.terrainData, changeArea, layer);
            }
        }

        public void InvokeOnTerrainTreeChange(Terrain terrain, bool hasRemovals)
        {
            if (OnTerrainTreeChange != null && terrain != null)
            {
                OnTerrainTreeChange(terrain.terrainData, hasRemovals);
            }
        }

        internal void InvokeTerrainCheck(Terrain terrain)
        {
            if (OnTerrainCheck != null && terrain != null)
            {
                OnTerrainCheck(terrain.terrainData);
            }
        }
    }
}
