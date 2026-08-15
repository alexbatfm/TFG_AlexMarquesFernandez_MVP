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
        /// <param name="tipografiaDeVisor">True solo desde el arranque de Realidad Aumentada:
        /// la sección de sensores usa los cuerpos de letra grandes del visor. El escritorio
        /// conserva los suyos (ver la nota de tipografía en MetadataPanelController).</param>
        public static void TryAttach(SceneModelIndex index, MetadataPanelController panel,
                                     bool tipografiaDeVisor = false)
        {
            var serviceGo = new GameObject("~MySqlSensorPollingService");
            Object.DontDestroyOnLoad(serviceGo);
            var service = serviceGo.AddComponent<MySqlSensorPollingService>();

            var section = new SensorPanelSection(service, tipografiaDeVisor);
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

            // LogWarning y no Log, y CadenaDeConexionSegura en vez de service.Host: la primera
            // razón es que las compilaciones que no son de desarrollo filtran Log del logcat y
            // esta línea es la que dice contra qué servidor se ha arrancado; la segunda, que
            // service.Host es el anfitrión de escritorio y en el visor se usa el remoto, así que
            // registrarlo aquí daba una dirección que no era la que se estaba usando.
            Debug.LogWarning($"[DigitalTwin][IoT] Middleware MySQL iniciado contra {service.CadenaDeConexionSegura}, " +
                             $"sondeo cada {service.PollIntervalSeconds}s. {index.Sensors.Count} sensores EQE... detectados en el modelo.");
        }
    }
}
