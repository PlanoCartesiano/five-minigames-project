using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Base class for syncing an undo operation. Undo operations are recorded on our custom undo stack by calling
     * sfUndoManager.Get().Record(sfIUndoOperation). When the operation is undone or redone, the corresponding method
     * is called on the operation to sync the changes.
     */
    public abstract class sfBaseUndoOperation
    {
        /**
         * Game objects affected by the operation.
         */
        public virtual GameObject[] GameObjects
        {
            get { return null; }
        }

        /**
         * Can this undo operation be combined with another operation?
         */
        public virtual bool CanCombine
        {
            get { return false; }
        }

        /**
         * Called to sync changes by an undo or redo operation.
         * 
         * @param   bool isUndo - true if this is an undo operation, false if it is a redo.
         */
        public abstract void HandleUndoRedo(bool isUndo);

        /**
         * Combines another operation with this one.
         * 
         * @return  bool true if the operations could be combined.
         */
        public virtual bool CombineWith(sfBaseUndoOperation other)
        {
            return false;
        }
    }
}
