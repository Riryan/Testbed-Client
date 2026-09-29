using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Root
{
    /// <summary>
    /// Client-only drag behavior for an authored managed window. The window remains authored
    /// in ClientUIRoot.prefab; this component only changes its local anchored position and
    /// stores that presentation preference locally. No gameplay/network state is involved.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientDraggableWindow : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private const float DefaultTitleBarHeight = 38f;
        private RectTransform _rect;
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private ClientUIPanelId _panelId;
        private bool _dragging;
        private bool _restored;

        public void Configure(ClientUIPanelId panelId)
        {
            _panelId = panelId;
            EnsureReferences();
            RestoreLocalPosition();
        }

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            EnsureReferences();
#endif
        }

        private void OnEnable()
        {
#if !UNITY_SERVER
            EnsureReferences();
            RestoreLocalPosition();
            ClampToCanvas();
#endif
        }

        public void OnPointerDown(PointerEventData eventData)
        {
#if !UNITY_SERVER
            if (!CanStartDrag(eventData)) return;
            transform.SetAsLastSibling();
#endif
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
#if !UNITY_SERVER
            _dragging = CanStartDrag(eventData);
            if (_dragging)
                transform.SetAsLastSibling();
#endif
        }

        public void OnDrag(PointerEventData eventData)
        {
#if !UNITY_SERVER
            if (!_dragging || _rect == null || _canvas == null)
                return;
            float scale = Mathf.Max(0.0001f, _canvas.scaleFactor);
            _rect.anchoredPosition += eventData.delta / scale;
            ClampToCanvas();
#endif
        }

        public void OnEndDrag(PointerEventData eventData)
        {
#if !UNITY_SERVER
            if (!_dragging) return;
            _dragging = false;
            ClampToCanvas();
            SaveLocalPosition();
#endif
        }

        private bool CanStartDrag(PointerEventData eventData)
        {
            EnsureReferences();
            if (_rect == null || eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return false;

            GameObject hit = eventData.pointerPressRaycast.gameObject;
            if (hit != null)
            {
                Selectable selectable = hit.GetComponentInParent<Selectable>();
                if (selectable != null && selectable.transform != transform)
                    return false;
            }

            Camera camera = eventData.pressEventCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, eventData.position, camera, out Vector2 local))
                return false;
            return local.y >= _rect.rect.yMax - DefaultTitleBarHeight;
        }

        private void EnsureReferences()
        {
            if (_rect == null) _rect = transform as RectTransform;
            if (_canvas == null) _canvas = GetComponentInParent<Canvas>();
            if (_canvas != null && _canvasRect == null)
                _canvasRect = _canvas.rootCanvas != null ? _canvas.rootCanvas.transform as RectTransform : _canvas.transform as RectTransform;
        }

        private string PositionKey => "MMO.UI.WindowPos." + (int)_panelId;

        private void RestoreLocalPosition()
        {
            if (_restored || _rect == null || _panelId == ClientUIPanelId.None)
                return;
            _restored = true;
            string key = PositionKey;
            if (!PlayerPrefs.HasKey(key + ".x") || !PlayerPrefs.HasKey(key + ".y"))
                return;
            _rect.anchoredPosition = new Vector2(PlayerPrefs.GetFloat(key + ".x"), PlayerPrefs.GetFloat(key + ".y"));
        }

        private void SaveLocalPosition()
        {
            if (_rect == null || _panelId == ClientUIPanelId.None)
                return;
            string key = PositionKey;
            PlayerPrefs.SetFloat(key + ".x", _rect.anchoredPosition.x);
            PlayerPrefs.SetFloat(key + ".y", _rect.anchoredPosition.y);
        }

        private void ClampToCanvas()
        {
            EnsureReferences();
            if (_rect == null || _canvasRect == null)
                return;

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_canvasRect, _rect);
            Rect canvasBounds = _canvasRect.rect;
            Vector2 shift = Vector2.zero;
            if (bounds.min.x < canvasBounds.xMin) shift.x += canvasBounds.xMin - bounds.min.x;
            if (bounds.max.x > canvasBounds.xMax) shift.x -= bounds.max.x - canvasBounds.xMax;
            if (bounds.min.y < canvasBounds.yMin) shift.y += canvasBounds.yMin - bounds.min.y;
            if (bounds.max.y > canvasBounds.yMax) shift.y -= bounds.max.y - canvasBounds.yMax;
            _rect.anchoredPosition += shift;
        }
    }
}
