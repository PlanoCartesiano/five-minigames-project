using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KS.Reactor;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Maps Keys of type K to event handlers of delegate type T.
     */
    public class sfEventMap<K, T> where T : class
    {
        protected Dictionary<K, ksEvent<T>> m_map;

        /**
         * Gets the event for a key.
         */
        public ksEvent<T> this[K key]
        {
            get
            {
                if (m_map == null)
                {
                    m_map = new Dictionary<K, ksEvent<T>>();
                }
                ksEvent<T> ev;
                if (!m_map.TryGetValue(key, out ev))
                {
                    ev = new ksEvent<T>();
                    m_map[key] = ev;
                }
                return ev;
            }

            set
            {
                // Do nothing. This setter is needed to make += and -= syntax work.
            }
        }

        /**
         * Gets a delegate that combines all event handlers for a key.
         * 
         * @param   K key to get handlers for.
         * @return  T delegate for the handlers, or null if the key has no handlers.
         */
        public virtual T GetHandlers(K key)
        {
            T handlers = null;
            if (m_map != null)
            {
                ksEvent<T> ev;
                if (m_map.TryGetValue(key, out ev))
                {
                    handlers = ev.Execute;
                }
            }
            return handlers;
        }
    }
}
