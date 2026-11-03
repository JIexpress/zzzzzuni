using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-AUDIT-03 (A-4) — regression lock for the repeated layer-canvas build on
    /// the re-adoption path.
    ///
    /// After a domain reload <c>UIRoot._instance</c> and the non-serialised
    /// <c>_layerCanvases</c> dictionary are gone, while the <c>[Layer:*]</c> children
    /// survive as scene objects. The old idempotency test only looked at the
    /// dictionary (<c>0 != 8</c>) and built a second full set: two stacked UIs, the old
    /// one still rendering/raycasting while UIManager only knew the new one.
    ///
    /// The reload is simulated by clearing the private static reference (there is
    /// deliberately no production seam for it). The case then asserts that
    /// EnsureCreated RE-USES the surviving 8 canvases — not that it merely reports 8.
    ///
    /// Mutation-verified: restoring the old <c>if (_layerCanvases.Count == 8) return;</c>
    /// check (dictionary only, no hierarchy re-index) turns this case red on
    /// <c>CountLayerHosts == 8</c> (it becomes 16) and on <c>AreSame(before[layer], ...)</c>.
    /// </summary>
    public sealed class UIRootReloadAdoptionPlayModeTests
    {
        private const string LayerHostPrefix = "[Layer:";

        [UnityTest]
        public IEnumerator ReAdoption_AfterStaticsAreLost_ReusesTheSurvivingLayerCanvases()
        {
            UIRoot root = UIRoot.EnsureCreated();
            Assert.IsNotNull(root, "precondition: EnsureCreated must return a live root");
            yield return null; // layer canvases register their raycaster this frame

            var canvasesBefore = new Dictionary<UIConsts.UILayer, Canvas>();
            foreach (UIConsts.UILayer layer in UIConsts.AllLayers)
            {
                Canvas canvas = root.GetLayerCanvas(layer);
                Assert.IsNotNull(canvas, "precondition: layer " + layer + " must own a canvas before the reload");
                canvasesBefore[layer] = canvas;
            }

            Assert.AreEqual(UIConsts.LayerCount, CountLayerHosts(root),
                "precondition: the root must start with exactly one [Layer:*] host per layer");

            // ---- the defect trigger: what a domain reload does ----
            SimulateDomainReload(root);
            Assert.IsNull(UIRoot.Instance, "precondition: the static reference is gone (the scene objects are not)");
            Assert.AreEqual(0, root.LayerCanvasCount,
                "precondition: the non-serialised canvas index is empty after the reload, while the children survive");
            Assert.AreEqual(UIConsts.LayerCount, CountLayerHosts(root),
                "precondition: the [Layer:*] children are still there — this is the state the old judge misread");

            UIRoot readopted = UIRoot.EnsureCreated();
            yield return null;

            Assert.AreSame(root, readopted, "the surviving root must be re-adopted instead of a second root being built");

            // The number the old case asserted (LayerCanvasCount) stays 8 in BOTH worlds —
            // that is exactly why it hid the defect. The hierarchy count is what has teeth.
            Assert.AreEqual(UIConsts.LayerCount, CountLayerHosts(readopted),
                "re-adoption built a second full set of layer canvases (16 hosts instead of 8): the old set keeps " +
                "rendering and raycasting while UIManager only knows the new one");

            Assert.AreEqual(UIConsts.LayerCount, readopted.LayerCanvasCount,
                "every adopted host must be indexed under its layer");

            foreach (UIConsts.UILayer layer in UIConsts.AllLayers)
            {
                Canvas canvas = readopted.GetLayerCanvas(layer);
                Assert.IsNotNull(canvas, "layer " + layer + " must be re-indexed from the surviving child");
                Assert.AreSame(canvasesBefore[layer], canvas,
                    "layer " + layer + " must map to the SURVIVING canvas (panels pushed before the reload live on it)");
                Assert.AreSame(readopted.transform, canvas.transform.parent,
                    "adopted layer canvases must stay direct children of the root");
                Assert.AreEqual(UIConsts.SortOrder(layer), canvas.sortingOrder,
                    "adopted layer " + layer + " kept the wrong sorting order");
                Assert.AreEqual(LayerHostPrefix + layer + "]", canvas.gameObject.name);
            }
        }

        /// <summary>
        /// Reproduces a domain reload on the live object: the static instance AND the
        /// non-serialised <c>_layerCanvases</c> index are gone, the <c>[Layer:*]</c>
        /// children are not. Reflection is used on purpose: production code exposes no
        /// "reset" seam and should not.
        /// </summary>
        private static void SimulateDomainReload(UIRoot root)
        {
            FieldInfo instance = typeof(UIRoot).GetField(
                "_instance", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(instance, "UIRoot._instance not found — the reload simulation is stale");
            instance.SetValue(null, null);

            FieldInfo canvases = typeof(UIRoot).GetField(
                "_layerCanvases", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(canvases, "UIRoot._layerCanvases not found — the reload simulation is stale");

            var index = canvases.GetValue(root) as System.Collections.IDictionary;
            Assert.IsNotNull(index, "UIRoot._layerCanvases is not an IDictionary — the reload simulation is stale");
            index.Clear();
        }

        private static int CountLayerHosts(UIRoot root)
        {
            int count = 0;
            for (int i = 0; i < root.transform.childCount; i++)
            {
                if (root.transform.GetChild(i).name.StartsWith(LayerHostPrefix, System.StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
