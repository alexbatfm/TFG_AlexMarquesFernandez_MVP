using System;
using DigitalTwin.UI;
using IFCImporter;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.IoT
{
    /// <summary>
    /// Fase 4: construye, dentro del panel de metadatos (Fase 2), la sección especial para
    /// sensores IoT (EQE...) con su valor y última lectura en tiempo real. Se engancha a
    /// MetadataPanelController.SensorSectionBuilder: el panel no sabe nada de MySQL ni de
    /// sensores, solo le da un hueco al principio para que esta clase dibuje si procede
    /// (devuelve 0 y no dibuja nada si el elemento clicado no es un sensor conocido).
    /// </summary>
    public class SensorPanelSection
    {
        private const float Height = 130f;

        private readonly MySqlSensorPollingService _service;

        public SensorPanelSection(MySqlSensorPollingService service)
        {
            _service = service;
        }

        public float Build(IfcMetadata meta, RectTransform container)
        {
            if (meta == null || string.IsNullOrEmpty(meta.globalId)) return 0f;
            if (!_service.Catalog.ByGlobalId.TryGetValue(meta.globalId, out var info)) return 0f;

            var bg = RuntimeUIFactory.CreatePanel(container, "SensorBg", new Color(0.16f, 0.38f, 0.58f, 0.35f));
            RuntimeUIFactory.StretchToParent((RectTransform)bg.transform);

            AddLabel(container, "Header", "SENSOR IoT · TIEMPO REAL", 12, -8, 18,
                new Color(0.6f, 0.85f, 1f, 1f), FontStyle.Bold);

            AddLabel(container, "Subtitle", $"{info.Name}  ·  {info.RoomName}", 13, -28, 18,
                new Color(0.85f, 0.85f, 0.85f, 1f));

            string valueLine;
            string statusLine;
            Color valueColor;

            if (_service.Store.TryGet(meta.globalId, out var reading))
            {
                valueLine = reading.Format(info.Kind);
                valueColor = _service.IsConnected ? Color.white : new Color(1f, 0.65f, 0.45f);
                statusLine = _service.IsConnected
                    ? $"Última lectura: {reading.RecordedAt:dd/MM/yyyy HH:mm}"
                    : $"Sin conexión con la base de datos · último dato conocido: {reading.RecordedAt:dd/MM/yyyy HH:mm}";
            }
            else
            {
                valueLine = "— sin datos todavía —";
                valueColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                statusLine = _service.IsConnected
                    ? "Esperando la primera lectura del sensor..."
                    : "Sin conexión con la base de datos (¿arrancado el contenedor Docker?)";
            }

            AddLabel(container, "Value", valueLine, 26, -50, 34, valueColor, FontStyle.Bold);
            AddLabel(container, "Status", statusLine, 11, -92, 30, new Color(0.75f, 0.75f, 0.75f, 1f));

            return Height;
        }

        private static void AddLabel(RectTransform parent, string name, string text, int fontSize, float y, float height, Color color, FontStyle style = FontStyle.Normal)
        {
            var label = RuntimeUIFactory.CreateText(parent, name, text, fontSize, TextAnchor.UpperLeft, color, style);
            var rect = (RectTransform)label.transform;
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(10, y);
            rect.sizeDelta = new Vector2(-20, height);
        }
    }
}
