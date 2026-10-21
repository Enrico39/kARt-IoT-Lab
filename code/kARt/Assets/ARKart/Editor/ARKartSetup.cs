using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace ARKart.EditorTools
{
    /// <summary>
    /// Menu "kARt" nell'editor: crea l'interfaccia e prepara scena e progetto
    /// senza doverli costruire a mano.
    /// </summary>
    public static class ARKartSetup
    {
        // ================================================================ 1. Interfaccia

        [MenuItem("kARt/1. Crea (o ricrea) l'interfaccia")]
        public static void BuildInterface()
        {
            var link = Object.FindFirstObjectByType<KartLink>();
            GameObject kart;
            if (link == null)
            {
                kart = new GameObject("kARt");
                Undo.RegisterCreatedObjectUndo(kart, "Crea kARt");
                link = Undo.AddComponent<KartLink>(kart);
            }
            else
            {
                kart = link.gameObject;
                kart.name = "kARt";
            }

            // Ricrea da zero i pannelli (i vecchi vengono rimossi)
            foreach (var oldName in new[] { "HUD Canvas", "HUD", "Video FPV" })
            {
                var old = kart.transform.Find(oldName);
                if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            }

            // niente "??": con gli oggetti Unity il confronto con null va fatto esplicitamente
            var hud = kart.GetComponent<KartHud>();
            if (hud == null) hud = Undo.AddComponent<KartHud>(kart);
            Undo.RecordObject(hud, "Collega HUD");
            hud.link = link;

            BuildVideoPanel(kart.transform, hud);
            BuildHud(kart.transform, hud);

            EditorUtility.SetDirty(hud);
            EditorSceneManager.MarkSceneDirty(kart.scene);
            Selection.activeGameObject = kart;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            Debug.Log("[kARt] Interfaccia creata: pannello video FPV + HUD. Salva la scena (Cmd+S).");
        }

        // --- Palette (uguale a KartHud) ---
        static readonly Color PanelBg = KartHud.Hex(0x0E1116, 0.86f);
        static readonly Color CardBg = KartHud.Hex(0x1A1F27, 0.92f);
        static readonly Color Line = new Color(1f, 1f, 1f, 0.08f);

        static Sprite RoundedSprite => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        static Sprite CircleSprite => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

        static void BuildVideoPanel(Transform parent, KartHud hud)
        {
            var canvas = WorldCanvas("Video FPV", parent, 1280, 720, new Vector3(0f, 1.5f, 2.2f), 0f, 0.001f);

            Img(Stretch("Cornice", canvas), KartHud.Hex(0x05070A, 0.92f), RoundedSprite, 0.3f);

            // Qui arriverà l'immagine della camera (RawImage con la texture dello stream)
            var videoRt = Stretch("Video", canvas, 14f);
            var video = videoRt.gameObject.AddComponent<RawImage>();
            video.color = KartHud.Hex(0x11161D);
            video.raycastTarget = false;

            Txt(Stretch("Segnaposto", canvas), "VIDEO FPV\n<size=30><color=#FFFFFF33>la camera della Pi apparirà qui</color></size>",
                72, new Color(1f, 1f, 1f, 0.22f), TextAlignmentOptions.Center, bold: true);

            // Mirino al centro
            Img(Rect("Mirino H", canvas, 620, 359, 40, 2), new Color(1f, 1f, 1f, 0.18f));
            Img(Rect("Mirino V", canvas, 639, 340, 2, 40), new Color(1f, 1f, 1f, 0.18f));

            // Indicatore dello stream
            var pill = Rect("Stato video", canvas, 30, 30, 180, 48);
            hud.videoStatusPill = Img(pill, new Color(1f, 1f, 1f, 0.12f), RoundedSprite, 0.42f);
            hud.videoStatus = Txt(Stretch("Testo", pill), "● OFFLINE", 22, KartHud.TextPrimary, TextAlignmentOptions.Center, bold: true, spacing: 4);
        }

        static void BuildHud(Transform parent, KartHud hud)
        {
            var canvas = WorldCanvas("HUD", parent, 1000, 340, new Vector3(0f, 0.95f, 1.6f), 25f, 0.0009f);

            Img(Stretch("Sfondo", canvas), PanelBg, RoundedSprite, 0.36f);

            // --- Intestazione ---
            Txt(Rect("Titolo", canvas, 32, 16, 300, 50), "k<color=#4C9AFF>AR</color>t", 42, KartHud.TextPrimary, TextAlignmentOptions.Left, bold: true);
            Txt(Rect("Sottotitolo", canvas, 32, 62, 420, 24), "Kart FPV · Mixed Reality", 17, KartHud.TextSecondary, TextAlignmentOptions.Left);

            var pill = Rect("Connessione", canvas, 712, 28, 256, 44);
            hud.connectionPill = Img(pill, new Color(KartHud.Green.r, KartHud.Green.g, KartHud.Green.b, 0.15f), RoundedSprite, 0.45f);
            hud.connectionDot = Img(Rect("Punto", pill, 20, 16, 12, 12), KartHud.Green, CircleSprite, 1f);
            hud.connectionText = Txt(Rect("Testo", pill, 42, 0, 206, 44), "CONNESSO", 19, KartHud.TextPrimary, TextAlignmentOptions.Left, bold: true, spacing: 3);

            Img(Rect("Divisore", canvas, 32, 98, 936, 2), Line);

            // --- Stato del kart ---
            var stateCard = Rect("Stato", canvas, 32, 118, 250, 150);
            Img(stateCard, CardBg, RoundedSprite, 0.55f);
            hud.stateAccent = Img(Rect("Accento", stateCard, 16, 22, 6, 106), KartHud.Green, RoundedSprite, 3f);
            Txt(Rect("Etichetta", stateCard, 38, 18, 200, 24), "STATO", 15, KartHud.TextSecondary, TextAlignmentOptions.Left, spacing: 6);
            hud.stateText = Txt(Rect("Valore", stateCard, 36, 44, 210, 84), "OK", 54, KartHud.Green, TextAlignmentOptions.Left, bold: true);

            var banner = Stretch("Stop di emergenza", stateCard);
            Img(banner, KartHud.Hex(0xEF4E4E, 0.95f), RoundedSprite, 0.55f);
            Txt(Stretch("Testo", banner, 10f), "STOP DI\nEMERGENZA\n<size=15>premi B per ripartire</size>", 26, Color.white, TextAlignmentOptions.Center, bold: true);
            hud.emergencyBanner = banner.gameObject;
            banner.gameObject.SetActive(false);

            // --- Comandi: barre centrate sullo zero ---
            hud.throttleFill = BarRow(canvas, "ACCELERATORE", 120, out hud.throttleValue);
            hud.steerFill = BarRow(canvas, "STERZO", 170, out hud.steerValue);
            hud.panFill = BarRow(canvas, "CAMERA", 220, out hud.panValue);

            // --- Sensore di parcheggio posteriore ---
            var rearCard = Rect("Dietro", canvas, 704, 118, 264, 150);
            Img(rearCard, CardBg, RoundedSprite, 0.55f);
            Txt(Rect("Etichetta", rearCard, 20, 18, 224, 24), "SENSORE POSTERIORE", 15, KartHud.TextSecondary, TextAlignmentOptions.Left, spacing: 4);
            hud.rearValue = Txt(Rect("Valore", rearCard, 20, 44, 224, 56), "libero", 40, KartHud.TextPrimary, TextAlignmentOptions.Left, bold: true);
            const int segments = 8;
            hud.rearSegments = new Image[segments];
            for (int i = 0; i < segments; i++)
                hud.rearSegments[i] = Img(Rect($"Segmento {i + 1}", rearCard, 20 + i * 28, 108, 24, 24), KartHud.Unlit, RoundedSprite, 2.5f);

            // --- Riga in basso ---
            Img(Rect("Divisore basso", canvas, 32, 288, 936, 2), Line);
            hud.footerText = Txt(Rect("Link", canvas, 32, 296, 936, 36), "RTT —", 18, KartHud.TextSecondary, TextAlignmentOptions.Left);
        }

        static Image BarRow(RectTransform canvas, string label, float y, out TMP_Text value)
        {
            Txt(Rect($"{label} etichetta", canvas, 306, y, 220, 24), label, 15, KartHud.TextSecondary, TextAlignmentOptions.Left, spacing: 4);
            value = Txt(Rect($"{label} valore", canvas, 486, y, 194, 24), "0", 19, KartHud.TextPrimary, TextAlignmentOptions.Right, bold: true);

            var track = Rect($"{label} barra", canvas, 306, y + 30, 374, 14);
            Img(track, new Color(1f, 1f, 1f, 0.08f), RoundedSprite, 1.4f);
            var fill = Img(Stretch("Riempimento", track), KartHud.Green, RoundedSprite, 1.4f);
            Img(Rect("Zero", track, 186, -4, 2, 22), new Color(1f, 1f, 1f, 0.45f));
            return fill;
        }

        // --- Mattoncini grafici ---

        static RectTransform WorldCanvas(string name, Transform parent, float w, float h, Vector3 pos, float tiltX, float scale)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(parent, false);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(w, h);
            rt.localPosition = pos;
            rt.localRotation = Quaternion.Euler(tiltX, 0f, 0f);
            rt.localScale = Vector3.one * scale;
            return rt;
        }

        // Rettangolo posizionato dall'angolo in alto a sinistra del genitore, in pixel
        static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        // Rettangolo che riempie il genitore (con margine opzionale)
        static RectTransform Stretch(string name, Transform parent, float margin = 0f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(margin, margin);
            rt.offsetMax = new Vector2(-margin, -margin);
            return rt;
        }

        static Image Img(RectTransform rt, Color color, Sprite sprite = null, float roundness = 1f)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = roundness;   // più alto = angoli meno arrotondati
            }
            return img;
        }

        static TextMeshProUGUI Txt(RectTransform rt, string text, float size, Color color,
            TextAlignmentOptions align, bool bold = false, float spacing = 0f)
        {
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            tmp.characterSpacing = spacing;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.raycastTarget = false;
            return tmp;
        }

        // ================================================================ 2. Progetto

        [MenuItem("kARt/2. Imposta il progetto per il Quest")]
        public static void ConfigureProject()
        {
            // UDP verso la Pi e, più avanti, stream video HTTP in chiaro sulla rete locale
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PlayerSettings.productName = "kARt";
            Debug.Log("[kARt] Progetto impostato: permesso Internet obbligatorio, HTTP in chiaro consentito, nome app kARt.");
            EditorUtility.DisplayDialog("kARt",
                "Fatto:\n• permesso Internet sempre attivo\n• HTTP in chiaro consentito (stream della camera)\n• nome dell'app: kARt", "OK");
        }

        // ================================================================ 3. Pulizia scena

        // Oggetti dimostrativi del modello "Mixed Reality" che kARt non usa
        static readonly string[] RootObjectsToRemove = { "UI", "Environment", "Permissions Manager" };
        static readonly string[] ChildObjectsToDisable = { "Goal Manager", "Object Spawner" };
        // Funzioni di AR Foundation non necessarie: rilevamento piani/mobili/volti, ancore, occlusione.
        // Restano attivi ARSession e ARCameraManager/ARCameraBackground, che servono al passthrough.
        static readonly string[] ComponentsToDisable =
        {
            "ARPlaneManager", "ARBoundingBoxManager", "ARFaceManager", "ARAnchorManager",
            "AROcclusionManager", "ARShaderOcclusion", "OcclusionManager",
            "ARFeatureController", "GoalManager", "ObjectSpawner", "SpawnedObjectsManager",
        };

        [MenuItem("kARt/3. Togli dalla scena la demo del modello Mixed Reality")]
        public static void CleanTemplateScene()
        {
            var report = new System.Text.StringBuilder();

            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (System.Array.IndexOf(RootObjectsToRemove, root.name) >= 0)
                {
                    report.AppendLine($"Rimosso: {root.name}");
                    Undo.DestroyObjectImmediate(root);
                    continue;
                }

                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (System.Array.IndexOf(ChildObjectsToDisable, t.name) >= 0 && t.gameObject.activeSelf)
                    {
                        Undo.RecordObject(t.gameObject, "Disattiva demo");
                        t.gameObject.SetActive(false);
                        report.AppendLine($"Disattivato: {t.name}");
                    }
                }

                foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null || !b.enabled) continue;
                    if (System.Array.IndexOf(ComponentsToDisable, b.GetType().Name) < 0) continue;
                    Undo.RecordObject(b, "Disattiva componente demo");
                    b.enabled = false;
                    report.AppendLine($"Spento: {b.GetType().Name} su {b.gameObject.name}");
                }
            }

            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            string text = report.Length > 0 ? report.ToString() : "Niente da togliere: la scena era già pulita.";
            Debug.Log("[kARt] Pulizia scena:\n" + text);
            EditorUtility.DisplayDialog("kARt", text + "\nSalva la scena con Cmd+S. Per tornare indietro: Cmd+Z.", "OK");
        }
    }
}
