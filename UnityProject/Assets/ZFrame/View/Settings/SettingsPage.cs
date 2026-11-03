using System;
using System.Collections.Generic;
using System.Linq;

namespace ZFrame.View
{
    /// <summary>
    /// One settings page (tab): an id + display name + the ordered list of
    /// option definitions (TASK-A-M3-02). The settings panel's tab bar is
    /// generated from the page list and each page's rows are generated from
    /// the definition list by <see cref="SettingsRowFactory"/> — a page is
    /// data all the way down.
    /// </summary>
    public sealed class SettingsPage
    {
        /// <summary>Stable page id (persisted nowhere, but used for tab lookup, e.g. "video").</summary>
        public string Id { get; }

        /// <summary>Localized display name on the tab button (e.g. "视频").</summary>
        public string DisplayName { get; }

        /// <summary>Ordered option definitions rendered top-to-bottom by the row factory.</summary>
        public IReadOnlyList<SettingDefinition> Settings { get; }

        /// <summary>Creates a page; ids of its definitions must be non-empty (uniqueness is enforced by the registry).</summary>
        public SettingsPage(string id, string displayName, IList<SettingDefinition> settings)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("page id must not be null/empty", nameof(id));
            }

            if (string.IsNullOrEmpty(displayName))
            {
                throw new ArgumentException("page display name must not be null/empty", nameof(id));
            }

            Id = id;
            DisplayName = displayName;
            Settings = settings != null
                ? Array.AsReadOnly(settings.ToArray())
                : throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>Number of definitions on this page (diagnostics/tests).</summary>
        public int SettingCount => Settings.Count;
    }
}
