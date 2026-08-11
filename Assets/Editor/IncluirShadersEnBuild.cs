using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DigitalTwin.EditorTools
{
    /// <summary>
    /// Registra en «Always Included Shaders» los sombreadores que el proyecto busca por nombre en
    /// tiempo de ejecucion.
    ///
    /// POR QUE HACE FALTA
    ///
    /// Unity solo incluye en la compilacion los sombreadores que encuentra referenciados desde
    /// algun material del proyecto. Los que se piden con <c>Shader.Find</c> son invisibles para ese
    /// analisis, de modo que <c>Shader.Find</c> devuelve <c>null</c> en el dispositivo aunque
    /// funcione perfectamente al pulsar Play. Es una diferencia entre editor y compilacion que no
    /// produce ningun aviso al compilar.
    ///
    /// En este proyecto costo un ciclo entero de depuracion: la excepcion resultante interrumpio el
    /// arranque de Realidad Aumentada a media ejecucion, y ni el middleware de sensores ni los
    /// mandos llegaron a crearse. Ver la nota de <c>RuntimeMaterials</c>.
    ///
    /// POR QUE AUTOMATICO Y NO A MANO
    ///
    /// La lista vive en los ajustes de Graphics, y ponerla a mano no deja rastro de por que esta
    /// ahi ni sobrevive a un clon limpio del repositorio. Hecho por script, se reaplica sola y se
    /// documenta a si misma. Es idempotente: si ya estan registrados, no toca nada.
    /// </summary>
    [InitializeOnLoad]
    public static class IncluirShadersEnBuild
    {
        static IncluirShadersEnBuild()
        {
            EditorApplication.delayCall += Asegurar;
        }

        private static void Asegurar()
        {
            var ajustes = AssetDatabase
                .LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")
                .OfType<GraphicsSettings>()
                .FirstOrDefault();

            if (ajustes == null) return;

            var so = new SerializedObject(ajustes);
            var lista = so.FindProperty("m_AlwaysIncludedShaders");
            if (lista == null) return;

            var yaIncluidos = new HashSet<Shader>();
            for (int i = 0; i < lista.arraySize; i++)
            {
                var s = lista.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (s != null) yaIncluidos.Add(s);
            }

            var anadidos = new List<string>();
            foreach (var nombre in DigitalTwin.Core.RuntimeMaterials.SombreadoresSinIluminacion)
            {
                var shader = Shader.Find(nombre);
                if (shader == null || yaIncluidos.Contains(shader)) continue;

                lista.InsertArrayElementAtIndex(lista.arraySize);
                lista.GetArrayElementAtIndex(lista.arraySize - 1).objectReferenceValue = shader;
                yaIncluidos.Add(shader);
                anadidos.Add(nombre);
            }

            if (anadidos.Count == 0) return;

            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log("[DigitalTwin] Sombreadores anadidos a Always Included Shaders para que " +
                      "Shader.Find los encuentre tambien en las compilaciones: " +
                      string.Join(", ", anadidos));
        }
    }
}
