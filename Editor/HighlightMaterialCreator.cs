using UnityEditor;
using UnityEngine;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Utilidad de editor que crea un material de resaltado a partir del shader
    /// "ProceduralHands/Highlight" del package y lo guarda en el proyecto, listo para asignarlo al
    /// "Resaltado por defecto" de una mano o al "Material de resaltado" de un grabbable.
    /// </summary>
    public static class HighlightMaterialCreator {

        // Nombre del shader de resaltado incluido en el package.
        const string ShaderName = "ProceduralHands/Highlight";

        /// <summary>Crea y guarda en el proyecto un material que usa el shader de resaltado.</summary>
        [MenuItem("Tools/Procedural Hands/Create Highlight Material")]
        public static void CreateHighlightMaterial() {
            // Buscamos el shader; si no está, avisamos (suele faltar si el package no importó o el proyecto no usa URP).
            var shader = Shader.Find(ShaderName);
            if (shader == null) {
                EditorUtility.DisplayDialog("Procedural Hands",
                    $"No se encontró el shader '{ShaderName}'. Asegúrate de que el package se importó correctamente y de que el proyecto usa URP.", "OK");
                return;
            }

            // Pedimos al usuario dónde guardar el material (dentro de la carpeta Assets).
            string path = EditorUtility.SaveFilePanelInProject(
                "Crear material de resaltado", "ProceduralHandsHighlight", "mat",
                "Elige dónde guardar el material de resaltado (dentro de la carpeta Assets del proyecto).");
            // Si el usuario cancela, no hacemos nada.
            if (string.IsNullOrEmpty(path))
                return;

            // Creamos el material con el shader y lo guardamos como asset en la ruta elegida.
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();

            // Lo seleccionamos y lo resaltamos en el panel Project para que el usuario lo localice.
            Selection.activeObject = material;
            EditorGUIUtility.PingObject(material);
            Debug.Log($"Procedural Hands: material de resaltado creado en '{path}'. Asígnalo al 'Resaltado por defecto' de cada mano (o al 'Material de resaltado' de un Grabbable).", material);
        }
    }
}
