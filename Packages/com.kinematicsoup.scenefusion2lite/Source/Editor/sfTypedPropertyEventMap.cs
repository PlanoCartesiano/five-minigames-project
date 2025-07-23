using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Maps property names to sfTypeEventMaps for delegates of type T.
     */
    public class sfTypedPropertyEventMap<T> where T : class
    {
        private Dictionary<string, sfTypeEventMap<T>> m_map = new Dictionary<string, sfTypeEventMap<T>>();

        /**
         * Adds a handler for a type and property name.
         * 
         * @param   string name of property to add handler for.
         * @param   T handler to add.
         */
        public void Add<Type>(string name, T handler)
        {
            GetOrCreateTypeMap(name).Add<Type>(handler);
        }

        /**
         * Removes a handler for a type and property name.
         * 
         * @param   string name of property to remove handler for.
         * @param   T handler to remove.
         */
        public void Remove<Type>(string name, T handler)
        {
            sfTypeEventMap<T> typeMap;
            if (m_map.TryGetValue(name, out typeMap))
            {
                typeMap.Remove<Type>(handler);
            }
        }

        /**
         * Gets the handlers for the given type and property name.
         * 
         * @param   Type type
         * @param   string name of property.
         * @return  T handlers
         */
        public T GetHandlers(Type type, string name)
        {
            sfTypeEventMap<T> typeMap;
            if (m_map.TryGetValue(name, out typeMap))
            {
                return typeMap.GetHandlers(type);
            }
            return null;
        }

        /**
         * Get the handlers for a type and property. If the property is a subproperty, eg. A.B.C, will get the handlers
         * for C, B, and A.
         */
        public T GetHandlers(Type type, sfBaseProperty property)
        {
            T handlers = null;
            while (property.ParentProperty != null)
            {
                if (!string.IsNullOrEmpty(property.Name))
                {
                    sfTypeEventMap<T> typeMap;
                    if (m_map.TryGetValue(property.Name, out typeMap))
                    {
                        T typeHandlers = typeMap.GetHandlers(type);
                        if (typeHandlers != null)
                        {
                            if (handlers == null)
                            {
                                handlers = typeHandlers;
                            }
                            else
                            {
                                handlers = (T)(object)Delegate.Combine(
                                        (Delegate)(object)handlers, (Delegate)(object)typeHandlers);
                            }
                        }
                    }
                }
                property = property.ParentProperty;
            }
            return handlers;
        }

        /**
         * Gets the type event map for the given name. Creates one if it does not already exist.
         * 
         * @param   string name of property.
         * @return  sfTypeEventMap<T>
         */
        private sfTypeEventMap<T> GetOrCreateTypeMap(string name)
        {
            sfTypeEventMap<T> typeMap;
            if (!m_map.TryGetValue(name, out typeMap))
            {
                typeMap = new sfTypeEventMap<T>();
                m_map[name] = typeMap;
            }
            return typeMap;
        }
    }
}
