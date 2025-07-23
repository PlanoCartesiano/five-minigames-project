using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KS.Reactor;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Maps types to event handlers of delegate type T.
     */
    public class sfTypeEventMap<T> : sfEventMap<Type, T> where T : class
    {
        /**
         * Gets a delegate that combines all handlers for a type and its ancestors.
         * 
         * @param   Type type to get handlers for.
         */
        public override T GetHandlers(Type type)
        {
            return GetHandlers(type, true);
        }

        /**
         * Gets a delegate that combines all event handlers for a type.
         * 
         * @param   Type type to get handlers for.
         * @param   bool checkInheritance - if true, will also get handlers for ancestor types.
         * @return  T delegate for the handlers, or null if the type has no handlers.
         */
        public T GetHandlers(Type type, bool checkInheritance)
        {
            T handlers = null;
            if (m_map != null)
            {
                if (checkInheritance)
                {
                    foreach (KeyValuePair<Type, ksEvent<T>> pair in m_map)
                    {
                        if (pair.Key.IsAssignableFrom(type))
                        {
                            if (handlers == null)
                            {
                                handlers = pair.Value.Execute;
                            }
                            else
                            {
                                handlers = (T)(object)Delegate.Combine(
                                    (Delegate)(object)handlers, (Delegate)(object)pair.Value.Execute);
                            }
                        }
                    }
                }
                else
                {
                    ksEvent<T> ev;
                    if (m_map.TryGetValue(type, out ev))
                    {
                        handlers = ev.Execute;
                    }
                }
            }
            return handlers;
        }

        /**
         * Adds a handler for a type Key.
         * 
         * @param   T handler to add.
         */
        public void Add<Key>(T handler)
        {
            this[typeof(Key)] += handler;
        }

        /**
         * Adds a handler for a type Key.
         * 
         * @param   Type type to add handler for.
         * @param   T handler to add.
         */
        public void Add(Type type, T handler)
        {
            this[type] += handler;
        }

        /**
         * Removes a handler for a type Key.
         * 
         * @param   T handler to remove.
         */
        public void Remove<Key>(T handler)
        {
            this[typeof(Key)] -= handler;
        }

        /**
         * Removes a handler for a type Key.
         * 
         * @param   Type type to remove handler for.
         * @param   T handler to remove.
         */
        public void Remove(Type type, T handler)
        {
            this[type] -= handler;
        }
    }
}
