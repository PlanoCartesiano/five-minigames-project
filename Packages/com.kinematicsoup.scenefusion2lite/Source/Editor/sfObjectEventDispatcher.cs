using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using KS.SceneFusion;
using KS.SceneFusion2.Client;
using KS.Reactor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * The object event dispatcher listens for object events and calls the corresponding functions on the translator
     * registered for the object's type.
     */
    public class sfObjectEventDispatcher
    {
        /**
         * @return  sfObjectEventDispatcher singleton instance.
         */
        public static sfObjectEventDispatcher Get()
        {
            return m_instance;
        }
        private static sfObjectEventDispatcher m_instance = new sfObjectEventDispatcher();

        /**
         * Initialize handler
         */
        public delegate void InitializeHandler();

        /**
         * Invoked after translators are initialized. Once this is fired, it is safe to access translators using
         * sfObjectEventDispatcher.Get().GetTranslator calls.
         */
        public event InitializeHandler OnInitialize;

        /**
         * Is the object event dispatcher running?
         */
        public bool IsActive
        {
            get { return m_active; }
        }

        private Dictionary<string, sfBaseTranslator> m_translatorMap = new Dictionary<string, sfBaseTranslator>();
        private List<sfBaseTranslator> m_translators = new List<sfBaseTranslator>();
        private bool m_active = false;

        /**
         * Registers a translator to handle events for a given object type.
         *
         * @param   string objectType the translator should handle events for.
         * @param   sfBaseTranslator translator to register.
         */
        public void Register(string objectType, sfBaseTranslator translator)
        {
            if (m_translatorMap.ContainsKey(objectType))
            {
                ksLog.Error(this, "Cannot register translator for '" + objectType +
                    "' because another translator is already registered for that type");
                return;
            }
            m_translatorMap[objectType] = translator;
            if (!m_translators.Contains(translator))
            {
                m_translators.Add(translator);
            }
        }

        /**
         * Calls Initialize on all translators. Invokes OnInitialize and removes all OnInitialize handlers.
         */
        public void InitializeTranslators()
        {
            foreach (sfBaseTranslator translator in m_translators)
            {
                translator.Initialize();
            }
            if (OnInitialize != null)
            {
                OnInitialize();
                OnInitialize = null;
            }
        }

        /**
         * Starts listening for events and calls OnSessionConnect on all registered translators.
         * 
         * @param   sfSession session to listen to events on.
         */
        public void Start(sfSession session)
        {
            if (m_active)
            {
                return;
            }
            m_active = true;
            if (session != null)
            {
                session.OnCreate += OnCreate;
                session.OnConfirmCreate += OnConfirmCreate;
                session.OnDelete += OnDelete;
                session.OnConfirmDelete += OnConfirmDelete;
                session.OnLock += OnLock;
                session.OnUnlock += OnUnlock;
                session.OnLockOwnerChange += OnLockOwnerChange;
                session.OnDirectLockChange += OnDirectLockChange;
                session.OnParentChange += OnParentChange;
                session.OnPropertyChange += OnPropertyChange;
                session.OnRemoveField += OnRemoveField;
                session.OnListAdd += OnListAdd;
                session.OnListRemove += OnListRemove;
            }
            sfSelectionWatcher.Get().OnSelect += OnSelect;
            sfSelectionWatcher.Get().OnDeselect += OnDeselect;
            foreach (sfBaseTranslator translator in m_translators)
            {
                try
                {
                    translator.OnSessionConnect();
                }
                catch (Exception e)
                {
                    ksLog.Error(this, "Error calling " + translator.GetType().Name + ".OnSessionConnect.", e);
                }
            }
        }

        /**
         * Stops listening for events and calls OnSessionDisconnect on all registered translators.
         * 
         * @param   sfSession session to stop listening to events on.
         */
        public void Stop(sfSession session)
        {
            if (!m_active)
            {
                return;
            }
            m_active = false;
            if (session != null)
            {
                session.OnCreate -= OnCreate;
                session.OnConfirmCreate -= OnConfirmCreate;
                session.OnDelete -= OnDelete;
                session.OnConfirmDelete -= OnConfirmDelete;
                session.OnLock -= OnLock;
                session.OnUnlock -= OnUnlock;
                session.OnLockOwnerChange -= OnLockOwnerChange;
                session.OnDirectLockChange -= OnDirectLockChange;
                session.OnParentChange -= OnParentChange;
                session.OnPropertyChange -= OnPropertyChange;
                session.OnRemoveField -= OnRemoveField;
                session.OnListAdd -= OnListAdd;
                session.OnListRemove -= OnListRemove;
            }
            sfSelectionWatcher.Get().OnSelect -= OnSelect;
            sfSelectionWatcher.Get().OnDeselect -= OnDeselect;
            foreach (sfBaseTranslator translator in m_translators)
            {
                try
                {
                    translator.OnSessionDisconnect();
                }
                catch (Exception e)
                {
                    ksLog.Error(this, "Error calling " + translator.GetType().Name + ".OnSessionDisconnect.", e);
                }
            }
        }

        /**
         * Creates an sfObject for a uobject by calling Create on each translator until one of them handles the request.
         *
         * @param   UObject uobj to create sfObject for.
         * @return  sfObject for the uobject. May be null.
         */
        public sfObject Create(UObject uobj)
        {
            if (uobj == null)
            {
                return null;
            }
            sfObject obj = null;
            foreach (sfBaseTranslator translator in m_translators)
            {
                if (translator.TryCreate(uobj, out obj))
                {
                    break;
                }
            }
            return obj;
        }

        /**
         * Gets the translator for an object.
         *
         * @param   sfObject obj to get translator for.
         * @return  sfBaseTranslator translator for the object, or null if there is no translator for the object's
         *          type.
         */
        public sfBaseTranslator GetTranslator(sfObject obj)
        {
            return obj == null ? null : GetTranslator(obj.Type);
        }

        /**
         * Gets the translator for the given type.
         *
         * @param   string type
         * @return  sfBaseTranslator translator for the type, or null if there is no translator for the given type.
         */
        public sfBaseTranslator GetTranslator(string type)
        {
            sfBaseTranslator translator;
            if (!m_translatorMap.TryGetValue(type, out translator))
            {
                ksLog.Error(this, "Unknown object type '" + type + "'.");
            }
            return translator;
        }

        /**
         * Gets the translator for an object.
         *
         * @param   sfObject obj to get translator for.
         * @return  T translator for the object, or null if there is no translator for the object's type.
         */
        public T GetTranslator<T>(sfObject obj) where T : sfBaseTranslator
        {
            return GetTranslator(obj) as T;
        }

        /**
         * Gets the translator for the given type.
         *
         * @param   string type
         * @return  T translator for the type, or null if there is no translator for the given type.
         */
        public T GetTranslator<T>(string type) where T : sfBaseTranslator
        {
            return GetTranslator(type) as T;
        }

        /**
         * Calls GetUObject on the translator for an sfObject.
         * 
         * @param   sfObject obj to get UObject for.
         * @param   UObject current value of the serialized property we are getting the UObject reference for.
         * @return  UObject for the sfObject.
         */
        public UObject GetUObject(sfObject obj, UObject current = null)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            return translator == null ? null : translator.GetUObject(obj, current);
        }

        /**
         * Calls OnCreate on the translator for an object.
         *
         * @param   sfObject obj that was created.
         * @param   int childIndex the object was created at.
         */
        public void OnCreate(sfObject obj, int childIndex)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnCreate(obj, childIndex);
            }
        }

        /**
         * Calls OnPropertyChange on the translator for an object.
         *
         * @param   sfBaseProperty property that changed.
         */
        public void OnPropertyChange(sfBaseProperty property)
        {
            sfBaseTranslator translator = GetTranslator(property.GetContainerObject());
            if (translator != null)
            {
                translator.OnPropertyChange(property);
            }
        }

        /**
         * Calls OnConfirmCreate on the translator for an object.
         * 
         * @param   sfObject obj that whose creation was confirmed.
         */
        public void OnConfirmCreate(sfObject obj)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnConfirmCreate(obj);
            }
        }

        /**
         * Calls OnDelete on the translator for an object.
         *
         * @param   sfObject obj that was deleted.
         */
        public void OnDelete(sfObject obj)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnDelete(obj);
            }
        }

        /**
         * Calls OnConfirmDelete on the translator for an object.
         * 
         * @param   sfObject obj that whose deletion was confirmed.
         * @param   bool unsubscribed - true if the deletion occurred because we unsubscribed from the object's parent.
         */
        public void OnConfirmDelete(sfObject obj, bool unsubscribed)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnConfirmDelete(obj, unsubscribed);
            }
        }

        /**
         * Calls OnLock on the translator for an object.
         *
         * @param   sfObject obj that was locked.
         */
        public void OnLock(sfObject obj)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnLock(obj);
            }
        }

        /**
         * Calls OnUnlock on the translator for an object.
         *
         * @param   sfObject obj that was unlocked.
         */
        public void OnUnlock(sfObject obj)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnUnlock(obj);
            }
        }

        /**
         * Calls OnLockOwnerChange on the translator for an object.
         *
         * @param   sfObject obj whose lock owner changed.
         */
        public void OnLockOwnerChange(sfObject obj)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnLockOwnerChange(obj);
            }
        }

        /**
         * Calls OnDirectLockChange on the translator for an object.
         *
         * @param   sfObject obj whose direct lock state changed.
         */
        public void OnDirectLockChange(sfObject obj)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnDirectLockChange(obj);
            }
        }

        /**
         * Calls OnParentChange on the translator for an object.
         *
         * @param   sfObject obj whose parent changed.
         * @param   int childIndex of the object. -1 if the object is a root.
         */
        public void OnParentChange(sfObject obj, int childIndex)
        {
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnParentChange(obj, childIndex);
            }
        }

        /**
         * Calls OnRemoveField on the translator for an object.
         *
         * @param   sfDictionaryProperty dict the field was removed from.
         * @param   string name of removed field.
         */
        public void OnRemoveField(sfDictionaryProperty dict, string name)
        {
            sfBaseTranslator translator = GetTranslator(dict.GetContainerObject());
            if (translator != null)
            {
                translator.OnRemoveField(dict, name);
            }
        }

        /**
         * Calls OnListAdd on the translator for an object.
         *
         * @param   sfListProperty list that elements were added to.
         * @param   int index elements were inserted at.
         * @param   int count - number of elements added.
         */
        public void OnListAdd(sfListProperty list, int index, int count)
        {
            sfBaseTranslator translator = GetTranslator(list.GetContainerObject());
            if (translator != null)
            {
                translator.OnListAdd(list, index, count);
            }
        }

        /**
         * Calls OnListRemove on the translator for an object.
         *
         * @param   sfListProperty list that elements were removed from.
         * @param   int index elements were removed from.
         * @param   int count - number of elements removed.
         */
        public void OnListRemove(sfListProperty list, int index, int count)
        {
            sfBaseTranslator translator = GetTranslator(list.GetContainerObject());
            if (translator != null)
            {
                translator.OnListRemove(list, index, count);
            }
        }

        /**
         * Calls OnSelect on the translator for a uobject.
         * 
         * @param   UObject uobj that was selected.
         */
        public void OnSelect(UObject uobj)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(uobj);
            if (obj == null || !obj.IsSyncing)
            {
                return;
            }
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnSelect(obj, uobj);
            }
        }

        /**
         * Calls OnDeselect on the translator for a uobject.
         * 
         * @param   UObject uobj that was deselected.
         */
        public void OnDeselect(UObject uobj)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(uobj);
            if (obj == null || !obj.IsSyncing)
            {
                return;
            }
            sfBaseTranslator translator = GetTranslator(obj);
            if (translator != null)
            {
                translator.OnDeselect(obj, uobj);
            }
        }
    }
}
