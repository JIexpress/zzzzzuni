using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ZFrame.Data.Save
{
    /// <summary>
    /// Read facade over one module's JSON section of a save file (a
    /// <see cref="JToken"/> wrapper). Every getter is null-safe: a missing
    /// key, a JSON null, a wrong-typed value or an empty reader yields the
    /// caller-supplied default instead of throwing.
    /// </summary>
    public sealed class SaveReader
    {
        private readonly JToken _source;

        /// <summary>
        /// Wraps <paramref name="source"/>. Null is allowed and behaves like
        /// an empty section (used when a module was added after the save was
        /// written and has no section in the file).
        /// </summary>
        public SaveReader(JToken source)
        {
            _source = source;
        }

        /// <summary>
        /// Looks up <paramref name="key"/>. Returns false when the reader is
        /// empty, the key is missing or the value is an explicit JSON null.
        /// </summary>
        public bool TryGet(string key, out JToken value)
        {
            value = null;
            if (_source == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            JToken token = _source[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token;
            return true;
        }

        /// <summary>Reads a string, or <paramref name="defaultValue"/> when missing/null/wrong-typed.</summary>
        public string GetString(string key, string defaultValue = null)
        {
            if (!TryGet(key, out JToken token) || !(token is JValue jValue) || jValue.Value == null)
            {
                return defaultValue;
            }

            return jValue.Value.ToString();
        }

        /// <summary>Reads an int, or <paramref name="defaultValue"/> when missing/null/wrong-typed.</summary>
        public int GetInt(string key, int defaultValue = 0)
        {
            if (!TryGet(key, out JToken token) || !(token is JValue jValue) || jValue.Value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(jValue.Value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return defaultValue;
            }
            catch (InvalidCastException)
            {
                return defaultValue;
            }
            catch (OverflowException)
            {
                return defaultValue;
            }
        }

        /// <summary>Reads a float, or <paramref name="defaultValue"/> when missing/null/wrong-typed.</summary>
        public float GetFloat(string key, float defaultValue = 0f)
        {
            if (!TryGet(key, out JToken token) || !(token is JValue jValue) || jValue.Value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToSingle(jValue.Value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return defaultValue;
            }
            catch (InvalidCastException)
            {
                return defaultValue;
            }
            catch (OverflowException)
            {
                return defaultValue;
            }
        }

        /// <summary>Reads a double, or <paramref name="defaultValue"/> when missing/null/wrong-typed.</summary>
        public double GetDouble(string key, double defaultValue = 0d)
        {
            if (!TryGet(key, out JToken token) || !(token is JValue jValue) || jValue.Value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToDouble(jValue.Value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return defaultValue;
            }
            catch (InvalidCastException)
            {
                return defaultValue;
            }
            catch (OverflowException)
            {
                return defaultValue;
            }
        }

        /// <summary>Reads a bool, or <paramref name="defaultValue"/> when missing/null/wrong-typed.</summary>
        public bool GetBool(string key, bool defaultValue = false)
        {
            if (!TryGet(key, out JToken token) || !(token is JValue jValue) || jValue.Value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToBoolean(jValue.Value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return defaultValue;
            }
            catch (InvalidCastException)
            {
                return defaultValue;
            }
        }
    }
}
