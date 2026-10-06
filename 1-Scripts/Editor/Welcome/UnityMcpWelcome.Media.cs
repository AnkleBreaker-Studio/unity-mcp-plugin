using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMCP.Editor.Welcome
{
    internal sealed partial class UnityMcpWelcome
    {
        private static Action PreserveFocus(VisualElement root)
        {
            var focused = root.focusController?.focusedElement as VisualElement;
            if (focused == null || !root.Contains(focused)) return null;
            string Key(VisualElement element) => element.GetType().Name + ":" + element.name + ":" +
                string.Join(" ", element.GetClasses()) + ":" + string.Join("|", element.Query<Label>().ToList().Select(label => label.text));
            string key = Key(focused);
            return () =>
            {
                if (root.Contains(focused)) { focused.Focus(); return; }
                var named = string.IsNullOrEmpty(focused.name) ? null : root.Q<VisualElement>(focused.name);
                if (named != null && named.focusable && Key(named) == key) { named.Focus(); return; }
                root.Query<VisualElement>().ToList().FirstOrDefault(element => element.focusable && Key(element) == key)?.Focus();
            };
        }

        internal static Image LiveImage(Func<Texture2D> source, string className, ScaleMode mode = ScaleMode.ScaleToFit,
            string path = null, Action<bool> availability = null, string fallbackPath = null)
        {
            var picture = new Image { scaleMode = mode, pickingMode = PickingMode.Ignore };
            picture.AddToClassList(className);
            bool attached = false;
            bool active = true;
            bool listening = false;
            void Update(string changed)
            {
                using var perf = new UnityMcpWelcomePerf.Scope("UI.BindImage");
                if (changed != null && path != null && changed != path && changed != fallbackPath) return;
                Texture2D texture = source();
                if (picture.image != texture)
                {
                    if (listening) UnityMcpWelcomeImages.Pin(picture.image as Texture2D, -1);
                    picture.image = texture;
                    if (listening) UnityMcpWelcomeImages.Pin(texture, 1);
                }
                picture.style.visibility = texture != null ? Visibility.Visible : Visibility.Hidden;
                availability?.Invoke(texture != null);
            }
            void Activate(bool visible)
            {
                active = visible;
                bool enable = active && attached;
                if (enable == listening) return;
                listening = enable;
                if (enable)
                {
                    UnityMcpWelcomeImages.Pin(picture.image as Texture2D, 1);
                    UnityMcpWelcomeServices.MediaChanged += Update;
                    Update(null);
                }
                else
                {
                    UnityMcpWelcomeServices.MediaChanged -= Update;
                    UnityMcpWelcomeImages.Pin(picture.image as Texture2D, -1);
                }
            }
            picture.userData = (Action<bool>)Activate;
            picture.RegisterCallback<AttachToPanelEvent>(_ => { attached = true; Activate(active); });
            picture.RegisterCallback<DetachFromPanelEvent>(_ => { attached = false; Activate(active); });
            picture.style.visibility = Visibility.Hidden;
            availability?.Invoke(false);
            return picture;
        }
    }
}
