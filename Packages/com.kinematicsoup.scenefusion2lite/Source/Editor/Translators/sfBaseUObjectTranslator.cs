using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.Reactor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Base class for translators that sync UObject properties using Unity's serialization system.
     */
    public class sfBaseUObjectTranslator : sfBaseTranslator
    {
        /**
         * sfProperty change event handler.
         * 
         * @param   UObject uobj whose property changed.
         * @param   sfBaseProperty property that changed. Null if the property was removed.
         * @return  bool if false, the default property hander that sets the property using Unity serialization will be
         *          called.
         */
        public delegate bool PropertyChangeHandler(UObject uobj, sfBaseProperty property);

        /**
         * Handler for a post property change event.
         * 
         * @param   UObject uobj whose property changed.
         * @param   sfBaseProperty property that changed.
         */
        public delegate void PostPropertyChangeHandler(UObject uobj, sfBaseProperty property);

        /**
         * Handler for a post uobject change event.
         * 
         * @param   UObject uobj whose property changed.
         */
        public delegate void PostUObjectChangeHandler(UObject uobj);

        protected sfTypedPropertyEventMap<PropertyChangeHandler> m_propertyChangeHandlers =
            new sfTypedPropertyEventMap<PropertyChangeHandler>();

        /**
         * Post property change event map.
         * 
         * @return sfTypedPropertyEventMap<PostPropertyChangeHandler>
         */
        public sfTypedPropertyEventMap<PostPropertyChangeHandler> PostPropertyChange
        {
            get { return m_postPropertyChangeHandlers; }
        }
        private sfTypedPropertyEventMap<PostPropertyChangeHandler> m_postPropertyChangeHandlers =
            new sfTypedPropertyEventMap<PostPropertyChangeHandler>();

        /**
         * Post UObject change event map.
         * 
         * @return  sfTypeEventMap<PostUObjectChangeHandler>
         */
        public sfTypeEventMap<PostUObjectChangeHandler> PostUObjectChange
        {
            get { return m_postUObjectChangeHandlers; }
        }
        private sfTypeEventMap<PostUObjectChangeHandler> m_postUObjectChangeHandlers =
            new sfTypeEventMap<PostUObjectChangeHandler>();

        /**
         * Called when an sfObject property changes.
         *
         * @param   sfBaseProperty property that changed.
         */
        public override void OnPropertyChange(sfBaseProperty property)
        {
            UObject uobj = sfObjectMap.Get().GetUObject(property.GetContainerObject());
            if (uobj == null || CallPropertyChangeHandlers(uobj, property))
            {
                return;
            }

            sfIMissingScript missingScript = uobj as sfIMissingScript;
            if (missingScript != null)
            {
                sfMissingScriptSerializer.Get().SerializeProperty(missingScript, property);
                return;
            }

            SerializedObject so = sfPropertyManager.Get().GetSerializedObject(uobj);
            SerializedProperty sprop = sfPropertyManager.Get().GetSerializedProperty(so, property);
            if (sprop != null)
            {
                sfPropertyManager.Get().SetValue(sprop, property);
                sfPropertyManager.Get().QueuePropertyChangeEvent(uobj, property);
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
            UObject uobj = sfObjectMap.Get().GetUObject(dict.GetContainerObject());
            if (uobj == null || CallPropertyChangeHandlers(uobj, dict, name))
            {
                return;
            }

            sfIMissingScript missingScript = uobj as sfIMissingScript;
            if (missingScript != null)
            {
                if (dict.GetDepth() == 0)
                {
                    missingScript.SerializedProperties.Remove(name);
                }
                else
                {
                    sfMissingScriptSerializer.Get().SerializeProperty(missingScript, dict);
                }
                return;
            }

            // TODO: If we add remove elements handlers which we needed in Unreal but may not need here, if this is an
            // array property and the default value is an empty array, call the remove elements handler for all elements
            SerializedObject so = sfPropertyManager.Get().GetSerializedObject(uobj);
            SerializedProperty sprop = sfPropertyManager.Get().GetSerializedProperty(so, dict, name);
            if (sprop != null)
            {
                sfPropertyManager.Get().SetToDefaultValue(sprop);
                sfPropertyManager.Get().QueuePropertyChangeEvent(uobj, dict, name);
            }
        }

        /**
         * Called when one or more elements are added to a list property.
         *
         * @param   sfListProperty list that elements were added to.
         * @param   int index elements were inserted at.
         * @param   int count - number of elements added.
         */
        public override void OnListAdd(sfListProperty list, int index, int count)
        {
            UObject uobj = sfObjectMap.Get().GetUObject(list.GetContainerObject());
            if (uobj == null || CallPropertyChangeHandlers(uobj, list))
            {
                return;
            }

            sfIMissingScript missingScript = uobj as sfIMissingScript;
            if (missingScript != null)
            {
                sfMissingScriptSerializer.Get().SerializeProperty(missingScript, list);
                return;
            }

            SerializedObject so = sfPropertyManager.Get().GetSerializedObject(uobj);
            SerializedProperty sprop = sfPropertyManager.Get().GetSerializedProperty(so, list);
            if (!sprop.isArray)
            {
                return;
            }
            for (int i = index; i < index + count; i++)
            {
                sprop.InsertArrayElementAtIndex(i);
                sfPropertyManager.Get().SetValue(sprop.GetArrayElementAtIndex(i), list[i]);
            }
            sfPropertyManager.Get().QueuePropertyChangeEvent(uobj, list);
        }

        /**
         * Called when one or more elements are removed from a list property.
         *
         * @param   sfListProperty list that elements were removed from.
         * @param   int index elements were removed from.
         * @param   int count - number of elements removed.
         */
        public override void OnListRemove(sfListProperty list, int index, int count)
        {
            UObject uobj = sfObjectMap.Get().GetUObject(list.GetContainerObject());
            if (uobj == null || CallPropertyChangeHandlers(uobj, list))
            {
                return;
            }

            sfIMissingScript missingScript = uobj as sfIMissingScript;
            if (missingScript != null)
            {
                sfMissingScriptSerializer.Get().SerializeProperty(missingScript, list);
                return;
            }

            SerializedObject so = sfPropertyManager.Get().GetSerializedObject(uobj);
            SerializedProperty sprop = sfPropertyManager.Get().GetSerializedProperty(so, list);
            if (!sprop.isArray)
            {
                return;
            }

            for (int i = index + count - 1; i >= index; i--)
            {
#if !UNITY_2021_2_OR_NEWER
                int oldLength = sprop.arraySize;
#endif
                sprop.DeleteArrayElementAtIndex(i);
#if !UNITY_2021_2_OR_NEWER
                // Unity has a weird behaviour where it sets non-null object reference elements to null instead of
                // actually deleting them, so you may have to call this twice to delete it.
                if (oldLength == sprop.arraySize)
                {
                    sprop.DeleteArrayElementAtIndex(i);
                }
#endif
            }
            sfPropertyManager.Get().QueuePropertyChangeEvent(uobj, list);
        }

        /**
         * Creates an sfObject for a UObject. Does nothing if one already exists.
         * 
         * @param   UObject uobj to create sfObject for.
         * @param   string type of sfObject to create.
         * @return  sfObject for the UObject, or null if an sfObject already existed.
         */
        public sfObject CreateObject(UObject uobj, string type)
        {
            sfObject obj = sfObjectMap.Get().GetOrCreateSFObject(uobj, type);
            if (obj == null)
            {
                return null;
            }
            sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
            sfPropertyManager.Get().CreateProperties(uobj, properties);
            return obj;
        }

        /**
         * Calls the property change handlers for an sfProperty.
         * 
         * @param   UObject uobj whose property changed.
         * @param   sfBaseProperty property that changed.
         * @return  bool true if the change event was handled.
         */
        public bool CallPropertyChangeHandlers(UObject uobj, sfBaseProperty property, string name = null)
        {
            PropertyChangeHandler handlers;
            if (!string.IsNullOrEmpty(name) && property.ParentProperty == null)
            {
                // Non-empty name with a root property means a field in the root dictionary was removed (set to the
                // default value). Call the handler with the property name and with null as the property.
                handlers = m_propertyChangeHandlers.GetHandlers(uobj.GetType(), name);
                property = null;
            }
            else
            {
                handlers = m_propertyChangeHandlers.GetHandlers(uobj.GetType(), property);
            }

            if (handlers != null)
            {
                foreach (bool result in handlers.GetInvocationList().Select(
                    (Delegate handler) => ((PropertyChangeHandler)handler)(uobj, property)))
                {
                    if (result)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /**
         * Calls post property change handlers for a property and uobject.
         * 
         * @param   UObject uobj with the property that changed.
         * @param   sfBaseProperty property that changed.
         * @param   string name - if non-empty, the name of the sub-property that was removed from a dictionary
         *          property.
         */
        public void CallPostPropertyChangeHandlers(UObject uobj, sfBaseProperty property, string name = null)
        {
            PostPropertyChangeHandler handlers;
            if (!string.IsNullOrEmpty(name) && (property == null || property.ParentProperty == null))
            {
                // Non-empty name with a root property means a field in the root dictionary was removed (set to the
                // default value). Call the handler with the property name and with null as the property.
                handlers = m_postPropertyChangeHandlers.GetHandlers(uobj.GetType(), name);
                property = null;
            }
            else
            {
                handlers = m_postPropertyChangeHandlers.GetHandlers(uobj.GetType(), property);
            }
            if (handlers != null)
            {
                handlers(uobj, property);
            }
        }

        /**
         * Calls post UObject change handlers for a uobject.
         * 
         * @param   UObject uobj with the property that changed.
         */
        public void CallPostUObjectChangeHandlers(UObject uobj)
        {
            PostUObjectChangeHandler handlers = m_postUObjectChangeHandlers.GetHandlers(uobj.GetType());
            if (handlers != null)
            {
                handlers(uobj);
            }
        }

        /**
         * Called when a uobject is selected. Sends a lock request for the object.
         * 
         * @param   sfObject obj for the selected uobject.
         * @param   UObject uobj that was selected.
         */
        public override void OnSelect(sfObject obj, UObject uobj)
        {
            obj.RequestLock();
        }

        /**
         * Called when a uobject is deselected. Releases the lock on the object.
         * 
         * @param   sfObject obj for the deselected uobject.
         * @param   UObject uobj that was deselected.
         */
        public override void OnDeselect(sfObject obj, UObject uobj)
        {
            obj.ReleaseLock();
        }
    }
}
