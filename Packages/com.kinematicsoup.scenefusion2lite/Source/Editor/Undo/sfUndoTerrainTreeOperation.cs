using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
    * Syncs changes made by a terrain tree undo operation.
    */
    public class sfUndoTerrainTreeOperation : sfBaseUndoOperation
    {
        private TerrainData m_terrainData;

        /**
         * Constructor
         * 
         * @param   terrainData that changed.
         */
        public sfUndoTerrainTreeOperation(TerrainData terrainData)
        {
            m_terrainData = terrainData;
        }

        /**
         * Syncs terrain tree changes from the undo or redo operation.
         * 
         * @param   bool isUndo - true if this is an undo operation, false if it is a redo.
         */
        public override void HandleUndoRedo(bool isUndo)
        {
            sfTerrainTranslator translator =
                sfObjectEventDispatcher.Get().GetTranslator<sfTerrainTranslator>(sfType.Terrain);
            translator.OnTreeChange(m_terrainData, true);
        }
    }
}
