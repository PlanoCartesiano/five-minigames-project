using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using KS.Reactor;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Provides a method of finding components on a game object by they type name. Each component can only be returned
     * once. This is used to get components by name when linking components with sfObjects. Each component is returned
     * once to prevent the component from being linked to multiple objects.
     */
    public class sfComponentFinder
    {
        /**
         * True if the components were found in the order they were requested.
         */
        public bool InOrder
        {
            get { return m_inOrder; }
        }

        private bool m_inOrder = true;
        private ksLinkedList<KeyValuePair<string, Component>> m_components =
            new ksLinkedList<KeyValuePair<string, Component>>();

        /**
         * Constructor
         * 
         * @param   GameObject gameObject to get components from.
         * @param   sfComponentTranslator translator
         */
        public sfComponentFinder(GameObject gameObject, sfComponentTranslator translator)
        {
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component != null && translator.IsSyncable(component))
                {
                    m_components.Add(
                        new KeyValuePair<string, Component>(sfComponentUtils.GetName(component), component));
                }
            }
        }

        /**
         * Gets the first component with the given type name that hasn't been returned yet. Each component returned
         * is removed from the finder.
         * 
         * @param   string name of component type to find. You can get this type name from a component using
         *          sfComponentUtils.GetName.
         * @return  Component component that matches the type name, or null if not found.
         */
        public Component Find(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                foreach (KeyValuePair<string, Component> pair in m_components)
                {
                    if (pair.Key == name)
                    {
                        m_components.RemoveCurrent();
                        return pair.Value;
                    }
                    m_inOrder = false;
                }
            }
            return null;
        }
    }
}
