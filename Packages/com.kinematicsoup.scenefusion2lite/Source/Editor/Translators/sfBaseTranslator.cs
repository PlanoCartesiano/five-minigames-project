using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using KS.SceneFusion2.Client;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Base class for handling object events.
     */
    public class sfBaseTranslator
    {
        /**
         * Called after all translators are registered. Do one time initialization here that depends on other
         * translators.
         */
        public virtual void Initialize() { }

        /**
         * Called after connecting to a session.
         */
        public virtual void OnSessionConnect() { }

        /**
         * Called after disconnecting from a session.
         */
        public virtual void OnSessionDisconnect() { }

        /**
         * Creates an sfObject for a uobject.
         *
         * @param   UObject uobj to create sfObject for.
         * @param   sfObject outObj created for the uobject.
         * @return  bool true if the uobject was handled by this translator.
         */
        public virtual bool TryCreate(UObject uobj, out sfObject outObj)
        {
            outObj = null;
            return false;
        }

        /**
         * Gets the UObject for an sfObject.
         * 
         * @param   sfObject obj to get UObject for.
         * @param   UObject current value of the serialized property we are getting the UObject reference for.
         * @return  UObject for the sfObject.
         */
        public virtual UObject GetUObject(sfObject obj, UObject current = null)
        {
            return sfObjectMap.Get().GetUObject(obj);
        }

        /**
         * Called when an object is created by another user.
         *
         * @param   sfObject obj that was created.
         * @param   int childIndex of new object. -1 if object is a root.
         */
        public virtual void OnCreate(sfObject obj, int childIndex) { }

        /**
         * Called when a locally created object is confirmed as created.
         * 
         * @param   sfObject obj that whose creation was confirmed.
         */
        public virtual void OnConfirmCreate(sfObject obj) { }

        /**
         * Called when an object is deleted by another user.
         *
         * @param   sfObject obj that was deleted.
         */
        public virtual void OnDelete(sfObject obj) { }

        /**
         * Called when a locally deleted object is confirmed as deleted.
         * 
         * @param   sfObject obj that whose deletion was confirmed.
         * @param   bool unsubscribed - true if the deletion occurred because we unsubscribed from the object's parent.
         */
        public virtual void OnConfirmDelete(sfObject obj, bool unsubscribed) { }

        /**
         * Called when an object is locked by another user.
         *
         * @param   sfObject obj that was locked.
         */
        public virtual void OnLock(sfObject obj) { }

        /**
         * Called when an object is unlocked by another user.
         *
         * @param   sfObject obj that was unlocked.
         */
        public virtual void OnUnlock(sfObject obj) { }

        /**
         * Called when an object's lock owner changes.
         *
         * @param   sfObject obj whose lock owner changed.
         */
        public virtual void OnLockOwnerChange(sfObject obj) { }

        /**
         * Called when an object's direct lock owner changes.
         *
         * @param   sfObject obj whose direct lock owner changed.
         */
        public virtual void OnDirectLockChange(sfObject obj) { }

        /**
         * Called when an object's parent is changed by another user.
         *
         * @param   sfObject obj whose parent changed.
         * @param   int childIndex of the object. -1 if the object is a root.
         */
        public virtual void OnParentChange(sfObject obj, int childIndex) { }

        /**
         * Called when an object property changes.
         *
         * @param   sfBaseProperty property that changed.
         */
        public virtual void OnPropertyChange(sfBaseProperty property) { }

        /**
         * Called when a field is removed from a dictionary property.
         *
         * @param   sfDictionaryProperty dict the field was removed from.
         * @param   string name of removed field.
         */
        public virtual void OnRemoveField(sfDictionaryProperty dict, string name) { }

        /**
         * Called when one or more elements are added to a list property.
         *
         * @param   sfListProperty list that elements were added to.
         * @param   int index elements were inserted at.
         * @param   int count - number of elements added.
         */
        public virtual void OnListAdd(sfListProperty list, int index, int count) { }

        /**
         * Called when one or more elements are removed from a list property.
         *
         * @param   sfListProperty list that elements were removed from.
         * @param   int index elements were removed from.
         * @param   int count - number of elements removed.
         */
        public virtual void OnListRemove(sfListProperty list, int index, int count) { }

        /**
         * Called when a Unity serialized property changes.
         * 
         * @param   sfObject obj whose property changed.
         * @param   SerializedProperty sprop that changed.
         * @return  bool false if the property change event should be handled by the default handler.
         */
        public virtual bool OnSPropertyChange(sfObject obj, SerializedProperty sprop) { return false; }

        /**
         * Called when a uobject is selected. The default implementation does nothing.
         * 
         * @param   sfObject obj for the selected uobject.
         * @param   UObject uobj that was selected.
         */
        public virtual void OnSelect(sfObject obj, UObject uobj) { }

        /**
         * Called when a uobject is deselected. The default implementation does nothing.
         * 
         * @param   sfObject obj for the deselected uobject.
         * @param   UObject uobj that was deselected.
         */
        public virtual void OnDeselect(sfObject obj, UObject uobj) { }
    }
}
