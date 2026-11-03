using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ZFrame.Data.Save
{
    /// <summary>
    /// Write facade over one module's <see cref="JObject"/> section of a save
    /// file (the JObject is wrapped, not exposed). Null values are stored as
    /// explicit JSON null instead of throwing.
    /// </summary>
    public sealed class SaveWriter
    {
        private readonly JObject _target;

        /// <summary>Wraps <paramref name="target"/>; every <see cref="Set"/> writes into it.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        public SaveWriter(JObject target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>Number of entries written so far (diagnostics/tests).</summary>
        public int Count
        {
            get { return _target.Count; }
        }

        /// <summary>
        /// Sets <paramref name="key"/> to <paramref name="value"/>. A null
        /// <paramref name="value"/> is stored as JSON null (null-safe).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
        public void Set(string key, JToken value)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("SaveWriter: key must not be null or empty.", nameof(key));
            }

            _target[key] = value ?? JValue.CreateNull();
        }

        /// <summary>Convenience for <see cref="Set(string, JToken)"/> with a string value (null-safe).</summary>
        public void Set(string key, string value)
        {
            Set(key, (JToken)(value == null ? JValue.CreateNull() : new JValue(value)));
        }

        /// <summary>Convenience for <see cref="Set(string, JToken)"/> with an int value.</summary>
        public void Set(string key, int value)
        {
            Set(key, new JValue(value));
        }

        /// <summary>Convenience for <see cref="Set(string, JToken)"/> with a float value (invariant culture).</summary>
        public void Set(string key, float value)
        {
            Set(key, new JValue(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
        }

        /// <summary>Convenience for <see cref="Set(string, JToken)"/> with a bool value.</summary>
        public void Set(string key, bool value)
        {
            Set(key, new JValue(value));
        }
    }
}
