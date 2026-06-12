using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Ventana de configuración paso a paso (<c>Tools &gt; Procedural Hands &gt; Setup Wizard</c>).
    /// Automatiza la configuración de física/capas y añade los componentes de mano/grabbable; la
    /// asignación de huesos y el guardado de poses se guían pero los hace el usuario (el flujo
    /// "física + componentes").
    /// </summary>
    public class ProceduralHandsSetupWizard : EditorWindow {

        // Pasos del asistente, en orden.
        enum Step { PhysicsAndLayers, CreateHand, AssignFingers, SavePoses, CreateGrabbable, Done }

        // Nombres de capa que el sistema necesita (NO traducir: son identificadores de capa reales).
        static readonly string[] RequiredLayers = { "Hand", "Grabbable", "Grabbing" };
        // Nombres de los presets de calidad física que se muestran en el desplegable.
        static readonly string[] QualityNames = { "Baja", "Media", "Alta (Quest)", "Muy alta" };
        // Títulos en español de cada paso (mismo orden que el enum Step).
        static readonly string[] StepNames = { "Física y capas", "Crear mano", "Asignar dedos", "Guardar poses", "Crear grabbable", "Listo" };

        Step step = Step.PhysicsAndLayers; // paso actual
        int physicsQuality = 2;            // preset de calidad seleccionado (por defecto "Alta (Quest)")
        GameObject handModel;              // modelo de mano riggeado asignado por el usuario
        bool handIsLeft;                   // si la mano a configurar es la izquierda
        Hand createdHand;                  // mano ya configurada (resultado del paso CreateHand)
        GameObject grabbableTarget;        // objeto a convertir en grabbable
        Vector2 scroll;                    // posición de scroll de la ventana

        // ===== Estado del asistente de pose automática (paso 4) =====
        // Modo de eje de flexión: Auto lo estima por la curvatura del dedo; el resto fuerza un eje local.
        enum FlexAxisMode { Auto, LocalX, LocalY, LocalZ }
        FlexAxisMode flexAxisMode = FlexAxisMode.Auto;
        bool invertFlex = false;       // invierte el sentido de cierre (si los dedos doblan hacia el dorso)
        float fistAmount = 0f;         // slider de cierre: 0 = abierta, 1 = puño
        float fistMaxAngle = 90f;      // ángulo máximo de flexión por articulación al cerrar del todo
        // Rotaciones locales base (postura abierta capturada) de cada hueso, para rotar desde ahí y poder restaurar.
        readonly Dictionary<Transform, Quaternion> poseBaseRotations = new Dictionary<Transform, Quaternion>();
        // Eje de flexión local (estimado) cacheado por hueso.
        readonly Dictionary<Transform, Vector3> flexAxisCache = new Dictionary<Transform, Vector3>();
        Hand poseAssistantHand;        // mano para la que se capturó la base

        /// <summary>Abre la ventana del asistente desde el menú de Unity.</summary>
        [MenuItem("Tools/Procedural Hands/Setup Wizard")]
        public static void Open() {
            var window = GetWindow<ProceduralHandsSetupWizard>("Procedural Hands Setup");
            window.minSize = new Vector2(380, 470);
            window.Show();
        }

        void OnGUI() {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("Procedural Hands — Asistente de configuración", EditorStyles.boldLabel);
            // Cabecera "Paso X / N: <nombre del paso>".
            EditorGUILayout.LabelField($"Paso {(int)step + 1} / {(int)Step.Done + 1}: {StepNames[(int)step]}");
            EditorGUILayout.Space();

            // Dibujamos la UI del paso actual.
            switch (step) {
                case Step.PhysicsAndLayers: DrawPhysicsAndLayers(); break;
                case Step.CreateHand: DrawCreateHand(); break;
                case Step.AssignFingers: DrawAssignFingers(); break;
                case Step.SavePoses: DrawSavePoses(); break;
                case Step.CreateGrabbable: DrawCreateGrabbable(); break;
                case Step.Done: DrawDone(); break;
            }

            EditorGUILayout.Space();
            // Botones Atrás/Siguiente.
            DrawNavigation();
            EditorGUILayout.EndScrollView();
        }

        //=================================================================
        //========================= PASOS =================================
        //=================================================================

        // Paso 1: crea las capas y aplica la configuración de física + las exclusiones de colisión.
        void DrawPhysicsAndLayers() {
            EditorGUILayout.HelpBox("Crea las capas Hand / Grabbable / Grabbing y aplica los ajustes de física recomendados y las exclusiones de colisión.", MessageType.Info);

            // Estado de las capas requeridas.
            bool layersOk = LayersExist();
            EditorGUILayout.LabelField("Capas", EditorStyles.boldLabel);
            foreach (var layer in RequiredLayers)
                EditorGUILayout.LabelField($"  {layer}: {(LayerMask.NameToLayer(layer) >= 0 ? "OK" : "falta")}");
            // El botón se deshabilita si ya existen todas las capas.
            using (new EditorGUI.DisabledScope(layersOk)) {
                if (GUILayout.Button("Crear capas"))
                    CreateLayers();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Física", EditorStyles.boldLabel);
            // Selector de preset de calidad.
            physicsQuality = EditorGUILayout.Popup("Calidad", physicsQuality, QualityNames);
            // Aplica de una vez los ajustes de física y las exclusiones de colisión entre capas.
            if (GUILayout.Button("Aplicar física + exclusiones de colisión")) {
                ApplyPhysicsSettings(physicsQuality);
                ApplyCollisionIgnores();
            }
        }

        // Paso 2: añade los componentes de mano al modelo riggeado.
        void DrawCreateHand() {
            EditorGUILayout.HelpBox("Asigna tu modelo de mano riggeado, elige el lado y añade los componentes.", MessageType.Info);
            handModel = (GameObject)EditorGUILayout.ObjectField("Modelo de mano", handModel, typeof(GameObject), true);
            handIsLeft = EditorGUILayout.Toggle("Es mano izquierda", handIsLeft);

            // El botón se habilita solo cuando hay un modelo asignado.
            using (new EditorGUI.DisabledScope(handModel == null)) {
                if (GUILayout.Button("Añadir componentes de mano"))
                    AddHandComponents();
            }

            // Resumen de lo que se ha configurado y de los ajustes manuales que quedan.
            if (createdHand != null)
                EditorGUILayout.HelpBox($"Mano lista en '{createdHand.name}'. Se añadieron Rigidbody, los módulos de Hand, Palm/PinchPoint y un XRHandControllerLink preconfigurado (el grip cierra los dedos, el botón de grip agarra). Coloca Palm/PinchPoint sobre la palma y asigna el 'Follow' de la mano al transform de tu controlador.", MessageType.None);
        }

        // Paso 3: detecta/asigna dedos y genera los colliders de la mano.
        void DrawAssignFingers() {
            EditorGUILayout.HelpBox("Si tus huesos de dedo siguen una convención de nombres, usa Auto-Detectar — coloca un Finger en cada nudillo y asigna las articulaciones. Reconoce dos estilos: descriptivo (Index/Middle/Ring/Pinky-o-Little/Thumb + Proximal/Intermediate/Distal/Tip) y numérico estilo Unreal/Mixamo (index_01/_02/_03, ...). Si no existe un hueso de punta, la genera al final de cada dedo. Si no, añádelos manualmente: selecciona un hueso de nudillo en la Jerarquía (NO la raíz de la mano) y pulsa el botón manual; luego asigna sus articulaciones en el inspector del Finger.", MessageType.Info);

            // Si perdimos la referencia a la mano, intentamos recuperarla desde la selección.
            if (createdHand == null)
                createdHand = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<Hand>() : null;

            // Detección automática por nombres de hueso.
            using (new EditorGUI.DisabledScope(createdHand == null)) {
                if (GUILayout.Button("Auto-Detectar dedos por nombre de hueso"))
                    AutoDetectFingers();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Manual", EditorStyles.miniBoldLabel);
            // Añade un Finger al hueso seleccionado (requiere mano y selección).
            using (new EditorGUI.DisabledScope(createdHand == null || Selection.activeGameObject == null)) {
                if (GUILayout.Button("Añadir Finger al hueso seleccionado"))
                    AddFingerToSelection();
            }

            // Listado del estado de cada dedo asignado.
            if (createdHand != null && createdHand.fingers != null) {
                EditorGUILayout.LabelField($"Dedos asignados: {createdHand.fingers.Length}", EditorStyles.boldLabel);
                foreach (var finger in createdHand.fingers) {
                    if (finger == null)
                        continue;
                    // Estado legible: faltan articulaciones / falta tipo / OK.
                    string status = finger.isMissingReferences ? "faltan articulaciones" : (finger.fingerType == FingerEnum.none ? "falta tipo" : "OK");
                    EditorGUILayout.LabelField($"  {finger.name} [{finger.fingerType}] — {status}");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Colliders de la mano", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox("Genera cápsulas a lo largo de los huesos de los dedos y una caja en la palma para que la mano interactúe físicamente. Vuelve a ejecutarlo si cambias el rig. Ajusta la caja de la palma después si hace falta.", MessageType.None);
            using (new EditorGUI.DisabledScope(createdHand == null)) {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Generar colliders de la mano"))
                    GenerateHandColliders(createdHand);
                if (GUILayout.Button("Limpiar", GUILayout.Width(70)))
                    ClearHandColliders(createdHand);
                EditorGUILayout.EndHorizontal();
            }
        }

        // Paso 4: guarda las poses abierta y cerrada de todos los dedos (a mano o con el asistente automático).
        void DrawSavePoses() {
            EditorGUILayout.HelpBox("Pon la mano plana y abierta y pulsa Guardar Abierta; luego haz un puño y pulsa Guardar Cerrada. O usa el asistente de abajo para generar el puño automáticamente.", MessageType.Info);
            if (createdHand == null) {
                EditorGUILayout.HelpBox("No hay mano seleccionada. Completa antes los pasos anteriores.", MessageType.Warning);
                return;
            }

            // --- Guardado manual: captura la postura ACTUAL de los huesos ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Guardar pose abierta")) SaveAllFingerPoses(createdHand, FingerPoseEnum.Open);
            if (GUILayout.Button("Guardar pose cerrada")) SaveAllFingerPoses(createdHand, FingerPoseEnum.Closed);
            EditorGUILayout.EndHorizontal();
            // Estado de las poses guardadas.
            EditorGUILayout.LabelField($"Abierta guardada: {AllHavePose(createdHand, FingerPoseEnum.Open)}    Cerrada guardada: {AllHavePose(createdHand, FingerPoseEnum.Closed)}");

            EditorGUILayout.Space();
            // --- Asistente automático: genera el puño rotando los huesos ---
            DrawPoseAssistant();
        }

        // Asistente que genera proceduralmente el puño (sin tocar el sistema runtime): rota los huesos en el editor
        // con previsualización en vivo y guarda las poses con el SavePose ya existente.
        void DrawPoseAssistant() {
            EditorGUILayout.LabelField("Asistente de pose automática", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Captura la postura ABIERTA actual y cierra los dedos automáticamente. Mueve el slider para previsualizar el puño; si los dedos se doblan hacia el dorso, activa 'Invertir sentido' o cambia el eje. Cuando te convenza: Guardar abierta + Guardar cerrada.", MessageType.None);

            // El asistente necesita una base capturada para ESTA mano antes de poder cerrar los dedos.
            bool ready = poseAssistantHand == createdHand && poseBaseRotations.Count > 0;
            if (!ready) {
                if (GUILayout.Button("Preparar asistente (capturar postura abierta actual)"))
                    CaptureFingerBase(createdHand);
                return;
            }

            // Controles de flexión: eje, sentido, ángulo máximo y cantidad de cierre (previsualización en vivo).
            EditorGUI.BeginChangeCheck();
            flexAxisMode = (FlexAxisMode)EditorGUILayout.EnumPopup("Eje de flexión", flexAxisMode);
            invertFlex = EditorGUILayout.Toggle("Invertir sentido", invertFlex);
            fistMaxAngle = EditorGUILayout.Slider("Ángulo de cierre", fistMaxAngle, 20f, 120f);
            fistAmount = EditorGUILayout.Slider("Cierre de dedos", fistAmount, 0f, 1f);
            // Al cambiar cualquier control, reaplicamos el puño a la postura base (registrando Undo).
            if (EditorGUI.EndChangeCheck()) {
                Undo.RegisterFullObjectHierarchyUndo(createdHand.gameObject, "Asistente de pose");
                ApplyFist(createdHand, fistAmount);
            }

            EditorGUILayout.BeginHorizontal();
            // Vuelve a la postura abierta capturada (slider a 0).
            if (GUILayout.Button("Restaurar abierta")) {
                Undo.RegisterFullObjectHierarchyUndo(createdHand.gameObject, "Asistente de pose");
                fistAmount = 0f;
                ApplyFist(createdHand, 0f);
            }
            // Recaptura la base con la postura actual (úsalo si ajustaste huesos a mano).
            if (GUILayout.Button("Recapturar base"))
                CaptureFingerBase(createdHand);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            Color prev = GUI.backgroundColor;
            EditorGUILayout.BeginHorizontal();
            // Guardar abierta: vuelve a la base (slider 0) y guarda esa postura como pose abierta.
            GUI.backgroundColor = AllHavePose(createdHand, FingerPoseEnum.Open) ? Color.green : new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button("Guardar abierta")) {
                Undo.RegisterFullObjectHierarchyUndo(createdHand.gameObject, "Asistente de pose");
                fistAmount = 0f;
                ApplyFist(createdHand, 0f);
                SaveAllFingerPoses(createdHand, FingerPoseEnum.Open);
            }
            // Guardar cerrada: guarda la postura actual (idealmente con el slider cerca de 1) como pose cerrada.
            GUI.backgroundColor = AllHavePose(createdHand, FingerPoseEnum.Closed) ? Color.green : new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button("Guardar cerrada"))
                SaveAllFingerPoses(createdHand, FingerPoseEnum.Closed);
            GUI.backgroundColor = prev;
            EditorGUILayout.EndHorizontal();
        }

        // Paso 5: convierte un objeto en grabbable.
        void DrawCreateGrabbable() {
            EditorGUILayout.HelpBox("Asigna un objeto con un collider (no trigger) para hacerlo agarrable.", MessageType.Info);
            grabbableTarget = (GameObject)EditorGUILayout.ObjectField("Objeto", grabbableTarget, typeof(GameObject), true);
            using (new EditorGUI.DisabledScope(grabbableTarget == null)) {
                if (GUILayout.Button("Hacer agarrable"))
                    MakeGrabbable();
            }
        }

        // Paso 6: pantalla final.
        void DrawDone() {
            EditorGUILayout.HelpBox("Configuración completa. Se añadió el XRHandControllerLink ya mapeado (grip / botón de grip). Asegúrate de que el 'Follow' de cada mano apunta al transform de su controlador y pulsa Play para probar.", MessageType.Info);
            if (GUILayout.Button("Cerrar"))
                Close();
        }

        // Barra de navegación: Atrás (deshabilitado en el primer paso) y Siguiente (deshabilitado en el último).
        void DrawNavigation() {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(step == Step.PhysicsAndLayers)) {
                if (GUILayout.Button("Atrás"))
                    step--;
            }
            using (new EditorGUI.DisabledScope(step == Step.Done)) {
                if (GUILayout.Button("Siguiente"))
                    step++;
            }
            EditorGUILayout.EndHorizontal();
        }

        //=================================================================
        //==================== ALTA DE COMPONENTES ========================
        //=================================================================

        // Añade y configura todos los componentes necesarios sobre el modelo de mano.
        void AddHandComponents() {
            // Si se asignó un prefab/asset, primero lo instanciamos en la escena.
            var go = EnsureSceneInstance(handModel);
            if (go == null)
                return;
            handModel = go;

            // Rigidbody sin gravedad y CON interpolación (la mano la mueve la física siguiendo al controlador;
            // la interpolación suaviza la posición dibujada entre pasos de física — sin ella la mano se vería
            // a saltos/ghosting cuando el visor renderiza a más Hz que la física).
            var rb = EnsureComponent<Rigidbody>(go);
            if (rb != null) {
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
            }

            // El componente Hand arrastra (vía RequireComponent) follower/animator/highlighter.
            var hand = EnsureComponent<Hand>(go);
            if (hand == null) {
                Debug.LogError("Procedural Hands: no se pudo añadir el componente Hand.", go);
                return;
            }
            hand.left = handIsLeft;

            // Rastreador de colisiones de la mano.
            EnsureComponent<CollisionTracker>(go);

            // Creamos los transforms Palm y PinchPoint si no existían (puntos de palma y de pinza).
            if (hand.palmTransform == null) {
                var palm = new GameObject("Palm");
                palm.transform.SetParent(go.transform, false);
                hand.palmTransform = palm.transform;
            }
            if (hand.pinchPointTransform == null) {
                var pinch = new GameObject("PinchPoint");
                pinch.transform.SetParent(go.transform, false);
                hand.pinchPointTransform = pinch.transform;
            }

            // Ponemos toda la jerarquía de la mano en la capa "Hand".
            int handLayer = LayerMask.NameToLayer("Hand");
            if (handLayer >= 0)
                SetLayerRecursive(go.transform, handLayer);

            // Añadimos y preconfiguramos el enlace de input del controlador (grip cierra dedos, botón de grip agarra).
            var link = EnsureComponent<XRHandControllerLink>(go);
            if (link != null)
                ConfigureControllerLink(link, hand);

            // Guardamos la mano resultante, la marcamos sucia y la seleccionamos.
            createdHand = hand;
            EditorUtility.SetDirty(hand);
            Selection.activeGameObject = go;
        }

        /// <summary>Rellena los huecos de acción de input vacíos del enlace con los bindings por defecto de OpenXR según el lado de la mano.</summary>
        static void ConfigureControllerLink(XRHandControllerLink link, Hand hand) {
            link.hand = hand;
            // Token de binding para el lado correcto del controlador.
            string side = hand.left ? "{LeftHand}" : "{RightHand}";

            // Un componente recién añadido tiene acciones inline NO nulas pero SIN bindings; por eso comprobamos el nº de bindings.
            // Eje de grip → analógico del grip.
            if (IsEmpty(link.gripAxis))
                link.gripAxis = new InputActionProperty(new InputAction("Grip", InputActionType.Value, $"<XRController>{side}/grip", expectedControlType: "Axis"));
            // Eje de squeeze → analógico del gatillo (trigger).
            if (IsEmpty(link.squeezeAxis))
                link.squeezeAxis = new InputActionProperty(new InputAction("Trigger", InputActionType.Value, $"<XRController>{side}/trigger", expectedControlType: "Axis"));
            // Acción de agarrar → botón del grip.
            if (IsEmpty(link.grabAction))
                link.grabAction = new InputActionProperty(new InputAction("Grab", InputActionType.Button, $"<XRController>{side}/gripButton"));
            // Acción de apretar → botón del gatillo.
            if (IsEmpty(link.squeezeAction))
                link.squeezeAction = new InputActionProperty(new InputAction("Squeeze", InputActionType.Button, $"<XRController>{side}/triggerButton"));

            EditorUtility.SetDirty(link);

            // Una InputActionProperty está "vacía" si no referencia un asset y su acción inline no tiene bindings.
            bool IsEmpty(InputActionProperty prop) {
                return prop.reference == null && (prop.action == null || prop.action.bindings.Count == 0);
            }
        }

        // Añade un Finger al GameObject seleccionado y lo registra en la lista de dedos de la mano.
        void AddFingerToSelection() {
            var sel = Selection.activeGameObject;
            if (sel == null || createdHand == null)
                return;

            var finger = EnsureComponent<Finger>(sel);
            if (finger == null)
                return;
            finger.hand = createdHand;
            // Intentamos adivinar el tipo de dedo por el nombre del objeto si aún no está puesto.
            if (finger.fingerType == FingerEnum.none)
                finger.fingerType = GuessFingerType(sel.name);

            // Lo añadimos al array de dedos de la mano si no estaba ya.
            var list = new List<Finger>(createdHand.fingers ?? new Finger[0]);
            if (!list.Contains(finger))
                list.Add(finger);
            createdHand.fingers = list.ToArray();
            EditorUtility.SetDirty(createdHand);
            EditorUtility.SetDirty(finger);
        }

        // Detecta los 5 dedos automáticamente buscando huesos por nombre y les asigna las articulaciones.
        void AutoDetectFingers() {
            if (createdHand == null) {
                EditorUtility.DisplayDialog("Procedural Hands", "Añade primero los componentes de mano (paso anterior).", "OK");
                return;
            }

            var root = createdHand.transform;
            // Todos los transforms de la jerarquía (incluidos inactivos) donde buscar los huesos.
            var all = root.GetComponentsInChildren<Transform>(true);

            // Partimos de cero: eliminamos cualquier Finger existente (incluidos los añadidos al objeto equivocado).
            foreach (var existing in root.GetComponentsInChildren<Finger>(true))
                Undo.DestroyObjectImmediate(existing);

            var result = new List<Finger>();
            int detected = 0;
            // Cada articulación admite VARIAS claves alternativas para soportar distintas convenciones de nombres:
            //  - Descriptiva (estilo XR Hands / Humanoid):  proximal / intermediate / distal / tip
            //  - Numérica   (estilo Unreal / Mixamo):       _01 / _02 / _03  (sin punta: se genera)
            string[] knuckleKeys = { "proximal", "01" };
            string[] middleKeys = { "intermediate", "02" };
            string[] distalKeys = { "distal", "03" };
            string[] tipKeys = { "tip", "04", "end" };
            // El nudillo del pulgar añade "metacarpal" (en la convención descriptiva el pulgar no tiene "intermediate").
            string[] thumbKnuckleKeys = { "metacarpal", "01" };
            string[] thumbMiddleKeys = { "proximal", "02" };

            detected += TrySetup(FingerEnum.index, new[] { "index" }, knuckleKeys, middleKeys, distalKeys, tipKeys);
            detected += TrySetup(FingerEnum.middle, new[] { "middle" }, knuckleKeys, middleKeys, distalKeys, tipKeys);
            detected += TrySetup(FingerEnum.ring, new[] { "ring" }, knuckleKeys, middleKeys, distalKeys, tipKeys);
            // El meñique puede llamarse "pinky" o "little".
            detected += TrySetup(FingerEnum.pinky, new[] { "pinky", "little" }, knuckleKeys, middleKeys, distalKeys, tipKeys);
            // El pulgar: nudillo = metacarpal/01, media = proximal/02, distal = distal/03.
            detected += TrySetup(FingerEnum.thumb, new[] { "thumb" }, thumbKnuckleKeys, thumbMiddleKeys, distalKeys, tipKeys);

            createdHand.fingers = result.ToArray();
            EditorUtility.SetDirty(createdHand);
            Debug.Log($"Procedural Hands: auto-detectados {detected}/5 dedos en '{createdHand.name}'.", createdHand);

            // Configura un dedo concreto: busca sus huesos y, si están los 3 principales, coloca/rellena el Finger.
            int TrySetup(FingerEnum type, string[] aliases, string[] knuckleK, string[] middleK, string[] distalK, string[] tipK) {
                Transform knuckle = Find(aliases, knuckleK);
                Transform middle = Find(aliases, middleK);
                Transform distal = Find(aliases, distalK);
                // Sin las tres articulaciones óseas no podemos montar el dedo: avisamos y se asigna a mano.
                if (knuckle == null || middle == null || distal == null) {
                    Debug.LogWarning($"Procedural Hands: no se pudo auto-detectar {type} " +
                        $"(nudillo:{(knuckle ? knuckle.name : "?")} media:{(middle ? middle.name : "?")} distal:{(distal ? distal.name : "?")}). Asígnalo manualmente.", createdHand);
                    return 0;
                }
                // La punta puede no existir como hueso (rigs numéricos): si no se encuentra, la generamos al final del distal.
                Transform tip = Find(aliases, tipK);
                if (tip == null)
                    tip = GenerateTip(aliases[0], knuckle, middle, distal);

                // Reutilizamos el Finger del nudillo si ya lo tiene; si no, lo añadimos.
                Finger finger = knuckle.GetComponent<Finger>();
                if (finger == null)
                    finger = Undo.AddComponent<Finger>(knuckle.gameObject);
                finger.hand = createdHand;
                finger.fingerType = type;
                finger.knuckleJoint = knuckle;
                finger.middleJoint = middle;
                finger.distalJoint = distal;
                finger.tip = tip;
                EditorUtility.SetDirty(finger);
                result.Add(finger);
                return 1;
            }

            // Busca el primer transform cuyo nombre contenga uno de los alias Y alguna de las claves de articulación.
            Transform Find(string[] aliases, string[] jointKeys) {
                foreach (var t in all) {
                    string n = t.name.ToLowerInvariant();
                    // ¿Coincide con algún alias del dedo?
                    bool aliasMatch = false;
                    for (int i = 0; i < aliases.Length; i++)
                        if (n.Contains(aliases[i])) { aliasMatch = true; break; }
                    if (!aliasMatch)
                        continue;
                    // ¿Y con alguna de las claves de la articulación (probadas en orden de preferencia)?
                    for (int k = 0; k < jointKeys.Length; k++)
                        if (n.Contains(jointKeys[k]))
                            return t;
                }
                return null;
            }

            // Genera (o reutiliza) un transform de punta al final del hueso distal, extendiendo el último segmento del dedo.
            Transform GenerateTip(string aliasBase, Transform knuckle, Transform middle, Transform distal) {
                // Nombre sin número para que no choque con las claves _01/_02/_03 al re-detectar.
                string tipName = aliasBase + "_tip_PH";
                // Si ya existe (de una ejecución previa), lo reutilizamos en lugar de duplicarlo.
                var existing = distal.Find(tipName);
                if (existing != null)
                    return existing;

                // Creamos el objeto de punta como hijo del distal.
                var tipGO = new GameObject(tipName);
                Undo.RegisterCreatedObjectUndo(tipGO, "Create Finger Tip");
                var tip = tipGO.transform;
                tip.SetParent(distal, false);

                // Vector del último segmento óseo (media → distal): indica hacia dónde "apunta" el dedo.
                Vector3 segment = distal.position - middle.position;
                // Si ese segmento es degenerado, probamos nudillo → distal y, en último caso, el forward del distal.
                if (segment.sqrMagnitude < 1e-8f)
                    segment = distal.position - knuckle.position;
                if (segment.sqrMagnitude < 1e-8f)
                    segment = distal.forward * 0.02f;

                // Colocamos la punta extendiendo esa misma longitud más allá del distal (estimación de la falange ausente).
                tip.position = distal.position + segment;
                tip.rotation = distal.rotation;
                return tip;
            }
        }

        // Convierte un objeto en grabbable: lo instancia si es asset, le añade Rigidbody + Grabbable y lo pone en la capa Grabbable.
        void MakeGrabbable() {
            var go = EnsureSceneInstance(grabbableTarget);
            if (go == null)
                return;
            grabbableTarget = go;

            EnsureComponent<Rigidbody>(go);
            EnsureComponent<Grabbable>(go);

            // Sin collider no se puede agarrar: avisamos.
            if (go.GetComponentInChildren<Collider>() == null)
                EditorUtility.DisplayDialog("Procedural Hands", "Este objeto no tiene Collider. Añade un collider (no trigger) para poder agarrarlo.", "OK");

            // Lo movemos a la capa "Grabbable".
            int grabbableLayer = LayerMask.NameToLayer("Grabbable");
            if (grabbableLayer >= 0)
                go.layer = grabbableLayer;
            EditorUtility.SetDirty(go);
            Selection.activeGameObject = go;
        }

        /// <summary>Devuelve una instancia en escena de <paramref name="go"/>, instanciándolo antes si se asignó un prefab/asset.</summary>
        static GameObject EnsureSceneInstance(GameObject go) {
            if (go == null)
                return null;
            // Si ya está en una escena, lo usamos tal cual.
            if (go.scene.IsValid())
                return go;

            // Si es un asset, intentamos instanciarlo como prefab; si no, hacemos una copia normal.
            var instance = PrefabUtility.InstantiatePrefab(go) as GameObject;
            if (instance == null)
                instance = UnityEngine.Object.Instantiate(go);
            instance.name = go.name;
            // Registramos la creación para Undo y la seleccionamos.
            Undo.RegisterCreatedObjectUndo(instance, "Create Procedural Hands Object");
            Selection.activeGameObject = instance;
            return instance;
        }

        /// <summary>Obtiene el componente, añadiéndolo (con Undo) si falta; en caso de éxito nunca devuelve null.</summary>
        static T EnsureComponent<T>(GameObject go) where T : Component {
            var component = go.GetComponent<T>();
            // Si no lo tiene, lo añadimos con Undo.
            if (component == null)
                component = Undo.AddComponent<T>(go);
            // Re-obtenemos por si AddComponent devolvió null (puede pasar con assets); así devolvemos la referencia real.
            if (component == null)
                component = go.GetComponent<T>();
            return component;
        }

        // Adivina el tipo de dedo a partir del nombre del objeto.
        static FingerEnum GuessFingerType(string name) {
            string n = name.ToLowerInvariant();
            if (n.Contains("index")) return FingerEnum.index;
            if (n.Contains("middle")) return FingerEnum.middle;
            if (n.Contains("ring")) return FingerEnum.ring;
            // El meñique puede llamarse "pinky" o "little".
            if (n.Contains("pinky") || n.Contains("little")) return FingerEnum.pinky;
            if (n.Contains("thumb")) return FingerEnum.thumb;
            return FingerEnum.none;
        }

        // Asigna una capa a un transform y a toda su descendencia (recursivo).
        static void SetLayerRecursive(Transform root, int layer) {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
                SetLayerRecursive(root.GetChild(i), layer);
        }

        //=================================================================
        //==================== COLLIDERS DE LA MANO =======================
        //=================================================================

        // Nombre de los antiguos objetos-contenedor de collider (para limpiar versiones previas del generador).
        const string HandColliderName = "PH_Collider";

        // Genera cápsulas a lo largo de los huesos de cada dedo y una caja en la palma.
        static void GenerateHandColliders(Hand hand) {
            if (hand == null)
                return;
            // Limpiamos cualquier collider previo antes de regenerar.
            ClearHandColliders(hand);
            int layer = hand.gameObject.layer;

            if (hand.fingers != null) {
                foreach (var finger in hand.fingers) {
                    if (finger == null || finger.isMissingReferences)
                        continue;
                    // Radio mínimo de seguridad para que la cápsula no sea degenerada.
                    float r = Mathf.Max(0.005f, finger.tipRadius);
                    // Una cápsula por falange: nudillo→media, media→distal, distal→punta.
                    AddCapsule(finger.knuckleJoint, finger.middleJoint, r, layer);
                    AddCapsule(finger.middleJoint, finger.distalJoint, r, layer);
                    AddCapsule(finger.distalJoint, finger.tip, r, layer);
                }
            }

            // Caja de la palma.
            if (hand.palmTransform != null)
                AddPalmBox(hand.palmTransform, layer);

            Debug.Log("Procedural Hands: colliders de la mano generados. Ajusta el tamaño/centro de la caja de la palma si hace falta.", hand);
        }

        // Elimina los colliders generados (cápsulas en los huesos, caja de la palma y restos de versiones antiguas).
        static void ClearHandColliders(Hand hand) {
            if (hand == null)
                return;

            // Quitamos las cápsulas puestas directamente sobre los huesos de los dedos.
            if (hand.fingers != null) {
                foreach (var finger in hand.fingers) {
                    if (finger == null)
                        continue;
                    RemoveComponent<CapsuleCollider>(finger.knuckleJoint);
                    RemoveComponent<CapsuleCollider>(finger.middleJoint);
                    RemoveComponent<CapsuleCollider>(finger.distalJoint);
                }
            }

            // Quitamos la caja de la palma.
            if (hand.palmTransform != null)
                RemoveComponent<BoxCollider>(hand.palmTransform);

            // Limpiamos los antiguos objetos-contenedor que creaban versiones previas del generador.
            var legacy = new List<GameObject>();
            foreach (var t in hand.GetComponentsInChildren<Transform>(true))
                if (t.name == HandColliderName)
                    legacy.Add(t.gameObject);
            foreach (var go in legacy)
                Undo.DestroyObjectImmediate(go);
        }

        // Elimina (con Undo) un componente del transform, si lo tiene.
        static void RemoveComponent<T>(Transform t) where T : Component {
            if (t == null)
                return;
            var component = t.GetComponent<T>();
            if (component != null)
                Undo.DestroyObjectImmediate(component);
        }

        // Cápsula añadida directamente sobre el hueso del dedo. El centro/altura se calculan en espacio LOCAL del hueso
        // (InverseTransformPoint), así siguen siendo correctos aunque el rig tenga escala uniforme.
        static void AddCapsule(Transform joint, Transform child, float radius, int layer) {
            if (joint == null || child == null)
                return;
            joint.gameObject.layer = layer;
            var capsule = Undo.AddComponent<CapsuleCollider>(joint.gameObject);
            // Posición del hijo en el espacio local del hueso: define la dirección y la longitud de la cápsula.
            Vector3 localChild = joint.InverseTransformPoint(child.position);
            float length = localChild.magnitude;
            // Eje dominante = a lo largo de qué eje local se extiende el hueso.
            capsule.direction = DominantAxis(localChild);
            // Centro a mitad de camino hacia el hijo.
            capsule.center = localChild * 0.5f;
            capsule.radius = radius;
            // Altura = longitud del hueso, con un mínimo de un diámetro para que sea válida.
            capsule.height = Mathf.Max(length, radius * 2f);
        }

        // Caja por defecto para la palma (tamaño/centro aproximados; el usuario los ajusta luego).
        static void AddPalmBox(Transform palm, int layer) {
            palm.gameObject.layer = layer;
            var box = Undo.AddComponent<BoxCollider>(palm.gameObject);
            box.size = new Vector3(0.09f, 0.03f, 0.10f);
            box.center = new Vector3(0f, 0f, -0.01f);
        }

        // Devuelve el índice del eje (0=X,1=Y,2=Z) de mayor magnitud del vector.
        static int DominantAxis(Vector3 v) {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            if (x >= y && x >= z) return 0;
            if (y >= z) return 1;
            return 2;
        }

        // True si TODOS los dedos tienen guardada la pose indicada (y hay al menos un dedo).
        static bool AllHavePose(Hand hand, FingerPoseEnum pose) {
            if (hand.fingers == null || hand.fingers.Length == 0)
                return false;
            foreach (var finger in hand.fingers)
                if (finger == null || !finger.IsPoseSaved(pose))
                    return false;
            return true;
        }

        // Guarda la pose indicada (postura actual) en todos los dedos de la mano.
        static void SaveAllFingerPoses(Hand hand, FingerPoseEnum pose) {
            if (hand.fingers == null)
                return;
            foreach (var finger in hand.fingers) {
                if (finger == null)
                    continue;
                finger.SavePose(hand, finger, pose);
                EditorUtility.SetDirty(finger);
            }
        }

        // Captura la postura ACTUAL de los dedos como base (pose abierta) y estima el eje de flexión de cada hueso.
        void CaptureFingerBase(Hand hand) {
            poseBaseRotations.Clear();
            flexAxisCache.Clear();
            poseAssistantHand = hand;
            fistAmount = 0f;
            if (hand == null || hand.fingers == null)
                return;

            // Normal de la palma estimada (orienta el sentido de flexión de forma coherente entre todos los dedos).
            Vector3 palmNormal = EstimatePalmNormal(hand);

            foreach (var finger in hand.fingers) {
                if (finger == null || finger.isMissingReferences)
                    continue;
                // Guardamos la rotación local actual de las tres articulaciones móviles.
                StoreBase(finger.knuckleJoint);
                StoreBase(finger.middleJoint);
                StoreBase(finger.distalJoint);
                // Estimamos el eje de flexión local de cada una a partir de la cadena de huesos del dedo.
                flexAxisCache[finger.knuckleJoint] = EstimateFlexAxis(finger.knuckleJoint, finger.middleJoint, finger.distalJoint, palmNormal);
                flexAxisCache[finger.middleJoint] = EstimateFlexAxis(finger.middleJoint, finger.distalJoint, finger.tip, palmNormal);
                flexAxisCache[finger.distalJoint] = EstimateFlexAxis(finger.distalJoint, finger.tip, null, palmNormal);
            }

            // Función local: guarda la rotación local actual de un hueso como su base.
            void StoreBase(Transform joint) {
                if (joint != null)
                    poseBaseRotations[joint] = joint.localRotation;
            }
        }

        // Aplica el puño: rota cada hueso desde su base alrededor de su eje de flexión, proporcional a 'amount' (0..1).
        void ApplyFist(Hand hand, float amount) {
            if (hand == null || hand.fingers == null)
                return;
            foreach (var finger in hand.fingers) {
                if (finger == null || finger.isMissingReferences)
                    continue;
                // El pulgar cierra bastante menos que los demás dedos.
                float typeFactor = finger.fingerType == FingerEnum.thumb ? 0.5f : 1f;
                // Pesos por articulación para un puño natural (la media cierra más, la distal menos).
                ApplyJointBend(finger.knuckleJoint, 0.9f * typeFactor, amount);
                ApplyJointBend(finger.middleJoint, 1.0f * typeFactor, amount);
                ApplyJointBend(finger.distalJoint, 0.7f * typeFactor, amount);
            }
        }

        // Rota un hueso desde su rotación base alrededor de su eje de flexión local.
        void ApplyJointBend(Transform joint, float weight, float amount) {
            // Sin base guardada para ese hueso, no hacemos nada.
            if (joint == null || !poseBaseRotations.TryGetValue(joint, out var baseRot))
                return;
            Vector3 axis = GetFlexAxisLocal(joint);
            // Ángulo = ángulo máximo * peso de la articulación * cantidad de cierre, con el signo elegido.
            float angle = fistMaxAngle * weight * amount * (invertFlex ? -1f : 1f);
            // Partimos siempre de la base para que el slider sea reversible (no acumula).
            joint.localRotation = baseRot * Quaternion.AngleAxis(angle, axis);
        }

        // Devuelve el eje de flexión local según el modo (Auto usa el estimado; el resto fuerza un eje local).
        Vector3 GetFlexAxisLocal(Transform joint) {
            switch (flexAxisMode) {
                case FlexAxisMode.LocalX: return Vector3.right;
                case FlexAxisMode.LocalY: return Vector3.up;
                case FlexAxisMode.LocalZ: return Vector3.forward;
                default: return flexAxisCache.TryGetValue(joint, out var a) ? a : Vector3.right;
            }
        }

        // Estima el eje de flexión local de un hueso a partir de la curvatura natural de la cadena del dedo.
        Vector3 EstimateFlexAxis(Transform joint, Transform child, Transform grandchild, Vector3 palmNormal) {
            Vector3 axisWorld = Vector3.zero;
            // Con dos segmentos consecutivos, su producto vectorial da la normal del plano de flexión del dedo.
            if (child != null && grandchild != null) {
                Vector3 a = child.position - joint.position;
                Vector3 b = grandchild.position - child.position;
                axisWorld = Vector3.Cross(a, b);
            }
            // Fallback (dedo recto o sin "nieto"): eje perpendicular a la dirección del hueso y a la normal de la palma.
            if (axisWorld.sqrMagnitude < 1e-7f) {
                Vector3 dir = child != null ? child.position - joint.position : joint.forward;
                axisWorld = Vector3.Cross(dir, palmNormal);
                if (axisWorld.sqrMagnitude < 1e-7f)
                    axisWorld = joint.right; // último recurso
            }
            // Uniformizamos el signo respecto a la normal de la palma: así todos los dedos doblan en el mismo sentido.
            if (Vector3.Dot(axisWorld, palmNormal) < 0f)
                axisWorld = -axisWorld;
            // Lo pasamos al espacio local del hueso (AngleAxis se aplica sobre la rotación local).
            return joint.InverseTransformDirection(axisWorld.normalized);
        }

        // Estima la normal de la palma con los nudillos de índice, medio y meñique (o cae a la palma/mano).
        Vector3 EstimatePalmNormal(Hand hand) {
            Transform index = null, middle = null, pinky = null;
            foreach (var f in hand.fingers) {
                if (f == null) continue;
                if (f.fingerType == FingerEnum.index) index = f.knuckleJoint;
                else if (f.fingerType == FingerEnum.middle) middle = f.knuckleJoint;
                else if (f.fingerType == FingerEnum.pinky) pinky = f.knuckleJoint;
            }
            // Plano definido por el ancho de la mano (índice→meñique) y el largo (muñeca→dedo medio).
            if (index != null && middle != null && pinky != null) {
                Vector3 across = pinky.position - index.position;
                Vector3 along = middle.position - hand.transform.position;
                Vector3 n = Vector3.Cross(across, along);
                if (n.sqrMagnitude > 1e-7f)
                    return n.normalized;
            }
            // Fallbacks si faltan dedos: la normal de la palma o, en su defecto, la de la mano.
            if (hand.palmTransform != null)
                return hand.palmTransform.up;
            return hand.transform.up;
        }

        //=================================================================
        //==================== FÍSICA / CAPAS =============================
        //=================================================================

        // Aplica los ajustes de física globales según el preset de calidad elegido.
        static void ApplyPhysicsSettings(int quality) {
            switch (quality) {
                case 0: // Baja
                    Time.fixedDeltaTime = 1 / 50f; Physics.defaultContactOffset = 0.01f;
                    Physics.defaultSolverIterations = 10; Physics.defaultSolverVelocityIterations = 5; break;
                case 1: // Media
                    Time.fixedDeltaTime = 1 / 60f; Physics.defaultContactOffset = 0.0075f;
                    Physics.defaultSolverIterations = 15; Physics.defaultSolverVelocityIterations = 5; break;
                case 2: // Alta (Quest)
                    Time.fixedDeltaTime = 1 / 72f; Physics.defaultContactOffset = 0.005f;
                    Physics.defaultSolverIterations = 30; Physics.defaultSolverVelocityIterations = 15; break;
                default: // Muy alta
                    Time.fixedDeltaTime = 1 / 90f; Physics.defaultContactOffset = 0.0035f;
                    Physics.defaultSolverIterations = 50; Physics.defaultSolverVelocityIterations = 30; break;
            }
            // Velocidad angular máxima por defecto algo alta para que las manos puedan girar rápido.
            Physics.defaultMaxAngularSpeed = 35f;
            Debug.Log("Procedural Hands: ajustes de física aplicados.");
        }

        // Excluye colisiones mano-mano y mano-grabbing (la mano no choca consigo misma ni con lo que está agarrando).
        static void ApplyCollisionIgnores() {
            int hand = LayerMask.NameToLayer("Hand");
            int grabbing = LayerMask.NameToLayer("Grabbing");
            if (hand >= 0)
                Physics.IgnoreLayerCollision(hand, hand, true);
            if (hand >= 0 && grabbing >= 0)
                Physics.IgnoreLayerCollision(hand, grabbing, true);
            Debug.Log("Procedural Hands: exclusiones de colisión aplicadas.");
        }

        // True si ya existen todas las capas requeridas.
        static bool LayersExist() {
            foreach (var layer in RequiredLayers)
                if (LayerMask.NameToLayer(layer) < 0)
                    return false;
            return true;
        }

        // Crea las capas requeridas en el TagManager, ocupando los primeros slots de usuario libres.
        static void CreateLayers() {
            // Abrimos el TagManager como SerializedObject para poder editar el array de capas.
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");

            foreach (var name in RequiredLayers) {
                // Si la capa ya existe, no la recreamos.
                if (LayerExists(layers, name))
                    continue;
                bool created = false;
                // Los slots 0..7 son de Unity; empezamos en el 8 (primer slot de usuario).
                for (int i = 8; i < layers.arraySize; i++) {
                    var element = layers.GetArrayElementAtIndex(i);
                    // Ocupamos el primer slot vacío que encontremos.
                    if (string.IsNullOrEmpty(element.stringValue)) {
                        element.stringValue = name;
                        created = true;
                        Debug.Log($"Procedural Hands: capa '{name}' creada en el índice {i}.");
                        break;
                    }
                }
                // Si no había hueco, avisamos (hay que liberar alguna capa de usuario).
                if (!created)
                    Debug.LogError($"Procedural Hands: no se pudo crear la capa '{name}' — no hay slots de usuario libres. Libera una capa y reinténtalo.");
            }

            // Persistimos los cambios en el TagManager.
            tagManager.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        // True si en el array de capas existe una con ese nombre.
        static bool LayerExists(SerializedProperty layers, string name) {
            for (int i = 0; i < layers.arraySize; i++)
                if (layers.GetArrayElementAtIndex(i).stringValue == name)
                    return true;
            return false;
        }
    }
}
