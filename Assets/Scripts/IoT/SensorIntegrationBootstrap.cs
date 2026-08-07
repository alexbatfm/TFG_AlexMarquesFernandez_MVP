using System.Collections.Generic;
using DigitalTwin.Core;
using DigitalTwin.Metadata;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.IoT
{
    /// <summary>
    /// Punto de enganche del middleware IoT (Fases 3 y 4) con el resto del sistema. Crea el
    /// servicio de sondeo MySQL, lo conecta a la sección especial del panel de metadatos
    /// (Fase 4) y hace que el panel se refresque solo cuando llega un valor nuevo del sensor
    /// que se está mostrando en ese momento, sin que el usuario tenga que volver a clicar.
    /// </summary>
    public static class SensorIntegrationBootstrap
    {
        public static void TryAttach(SceneModelIndex index, MetadataPanelController panel)
        {
            var serviceGo = new GameObject("~MySqlSensorPollingService");
            Object.DontDestroyOnLoad(serviceGo);
            var service = serviceGo.AddComponent<MySqlSensorPollingService>();

            var section = new SensorPanelSection(service);
            panel.SensorSectionBuilder = section.Build;

            // GlobalId -> IfcMetadata de los sensores EQE... del modelo, para poder refrescar el
            // panel en caliente cuando llegue una lectura nueva de ese sensor concreto.
            var byGlobalId = new Dictionary<string, IfcMetadata>();
            foreach (var meta in index.Sensors)
                if (!string.IsNullOrEmpty(meta.globalId))
                    byGlobalId[meta.globalId] = meta;

            service.Store.OnSensorUpdated += globalId =>
            {
                if (byGlobalId.TryGetValue(globalId, out var meta))
                    panel.RefreshIfShowing(meta);
            };

            Debug.Log($"[DigitalTwin][IoT] Middleware MySQL iniciado ({service.Host}:{service.Port}/{service.Database}, " +
                      $"sondeo cada {service.PollIntervalSeconds}s). {index.Sensors.Count} sensores EQE... detectados en el modelo.");
        }
    }
}
