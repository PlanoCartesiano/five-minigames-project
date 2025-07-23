using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.AnimatedValues;
using KS.Reactor;
using KS.SceneFusion;
using KS.SceneFusion2.Client;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Terrain brush data.
     */
    public class sfTerrainBrush
    {
        /**
         * The terrain the brush origin is on.
         */
        public Terrain Terrain;

        /**
         * The index of the brush in the brush list.
         */
        public int Index;

        /**
         * The position of the origin of the brush on the terrain from [0, 1].
         */
        public Vector2 Position;

        /**
         * The rotation of the brush.
         */
        public float Rotation;

        /**
         * The size of the brush.
         */
        public float Size;
    }
}
