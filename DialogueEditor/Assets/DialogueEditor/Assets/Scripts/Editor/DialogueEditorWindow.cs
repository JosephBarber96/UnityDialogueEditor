﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace DialogueEditor
{
    public class DialogueEditorWindow : EditorWindow
    {
        public enum eInputState
        {
            Regular,
            PlacingOption,
            PlacingSpeech,
            ConnectingNode,         
            draggingPanel,
        }

        // Consts
        public const float TOOLBAR_HEIGHT = 17;
        public const float START_PANEL_WIDTH = 250;
        private const float PANEL_RESIZER_PADDING = 5;
        private const string WINDOW_NAME = "DIALOGUE_EDITOR_WINDOW";
        private const string HELP_URL = "https://josephbarber96.github.io/dialogueeditor.html";
        private const string CONTROL_NAME = "DEFAULT_CONTROL";
        public const int MIN_PANEL_WIDTH = 180;

        // Static properties
        public static bool NodeClickedOnThisUpdate { get; set; }
        private static UINode CurrentlySelectedNode { get; set; }

        // Private variables:     
        private NPCConversation CurrentAsset;           // The Conversation scriptable object that is currently being viewed/edited
        public static EditableSpeechNode ConversationRoot { get; private set; }    // The root node of the conversation
        private List<UINode> uiNodes;                   // List of all UI nodes

        // Selected asset logic
        private NPCConversation currentlySelectedAsset;
        private Transform newlySelectedAsset;

        // Right-hand display pannel vars
        private float panelWidth;
        private Rect panelRect;
        private GUIStyle panelStyle;
        private GUIStyle panelTitleStyle;
        private GUIStyle panelPropertyStyle;
        private Rect panelResizerRect;
        private GUIStyle resizerStyle;
        private UINode m_cachedSelectedNode;

        // Dragging information
        private bool dragging;
        private bool clickInBox;
        private Vector2 offset;
        private Vector2 dragDelta;

        // Input and input-state logic
        private eInputState m_inputState;
        private UINode m_currentPlacingNode = null;
        private UINode m_currentConnectingNode = null;
        private EditableConversationNode m_connectionDeleteParent, m_connectionDeleteChild;




        //--------------------------------------
        // Open window
        //--------------------------------------

        [MenuItem("Window/DialogueEditor")]
        public static DialogueEditorWindow ShowWindow()
        {
            return EditorWindow.GetWindow<DialogueEditorWindow>("Dialogue Editor");
        }

        [UnityEditor.Callbacks.OnOpenAsset(1)]
        public static bool OpenDialogue(int assetInstanceID, int line)
        {
            NPCConversation conversation = EditorUtility.InstanceIDToObject(assetInstanceID) as NPCConversation;

            if (conversation != null)
            {
                DialogueEditorWindow window = ShowWindow();
                window.LoadNewAsset(conversation);
                return true;
            }
            return false;
        }




        //--------------------------------------
        // Load New Asset
        //--------------------------------------

        public void LoadNewAsset(NPCConversation asset)
        {
            CurrentAsset = asset;
            Log("Loading new asset: " + CurrentAsset.name);

            // Clear all current UI Nodes
            uiNodes.Clear();

            // Reconstruct the conversation
            ReconstructEditableConversation();

            // Refresh the Editor window
            Recenter();
            Repaint();

#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
#endif
        }

        public void ReconstructEditableConversation()
        {
            // Deseralize the asset and get the conversation root
            EditableConversation conversation = CurrentAsset.DeserializeForEditor();

            if (conversation == null)
                conversation = new EditableConversation();

            ConversationRoot = conversation.GetRootNode();

            // If it's null, create a root
            if (ConversationRoot == null)
            {
                ConversationRoot = new EditableSpeechNode();
                ConversationRoot.EditorInfo.xPos = (Screen.width / 2) - (UISpeechNode.Width / 2);
                ConversationRoot.EditorInfo.yPos = 0;
                ConversationRoot.EditorInfo.isRoot = true;
                conversation.SpeechNodes.Add(ConversationRoot);
            }

            // Get a list of every node in the conversation
            List<EditableConversationNode> allNodes = new List<EditableConversationNode>();
            for (int i = 0; i < conversation.SpeechNodes.Count; i++)
                allNodes.Add(conversation.SpeechNodes[i]);
            for (int i = 0; i < conversation.Options.Count; i++)
                allNodes.Add(conversation.Options[i]);

            // For every node: 
            // Find the children and parents by UID
            for (int i = 0; i < allNodes.Count; i++)
            {
                // Remove duplicate parent UIDs
                HashSet<int> noDupes = new HashSet<int>(allNodes[i].parentUIDs);
                allNodes[i].parentUIDs.Clear();
                foreach (int j in noDupes)
                    allNodes[i].parentUIDs.Add(j);

                // Get parents by UIDs
                allNodes[i].parents = new List<EditableConversationNode>();
                for (int j = 0; j < allNodes[i].parentUIDs.Count; j++)
                {
                    allNodes[i].parents.Add(conversation.GetNodeByUID(allNodes[i].parentUIDs[j]));
                }

                // Construct the connections
                //
                // V1.03
                if (conversation.SaveVersion == (int)eSaveVersion.V1_03)
                {
                    // Construct Connections from the OptionUIDs and SpeechUIDs (which are now deprecated)
                    // This supports upgrading from V1.03 +

                    if (allNodes[i] is EditableSpeechNode)
                    {
                        // Speech options
                        int count = (allNodes[i] as EditableSpeechNode).OptionUIDs.Count;
                        for (int j = 0; j < count; j++)
                        {
                            int optionUID = (allNodes[i] as EditableSpeechNode).OptionUIDs[j];
                            EditableOptionNode option = conversation.GetOptionByUID(optionUID);

                            allNodes[i].Connections.Add(new OptionConnection(option));
                        }

                        // Speech following speech
                        {
                            int speechUID = (allNodes[i] as EditableSpeechNode).SpeechUID;
                            EditableSpeechNode speech = conversation.GetSpeechByUID(speechUID);

                            if (speech != null)
                            {
                                allNodes[i].Connections.Add(new SpeechConnection(speech));
                            }
                        }
                    }
                    else if (allNodes[i] is EditableOptionNode)
                    {
                        int speechUID = (allNodes[i] as EditableOptionNode).SpeechUID;
                        EditableSpeechNode speech = conversation.GetSpeechByUID(speechUID);
                        
                        if (speech != null)
                        {
                            allNodes[i].Connections.Add(new SpeechConnection(speech));
                        }
                    }
                }
                //
                // V1.10 +
                else
                {
                    // For each node..  Reconstruct the connections
                    for (int j = 0; j < allNodes[i].Connections.Count; j++)
                    {
                        if (allNodes[i].Connections[j] is SpeechConnection)
                        {
                            EditableSpeechNode speech = conversation.GetSpeechByUID(allNodes[i].Connections[j].NodeUID);
                            (allNodes[i].Connections[j] as SpeechConnection).Speech = speech;
                        }
                        else if (allNodes[i].Connections[j] is OptionConnection)
                        {
                            EditableOptionNode option = conversation.GetOptionByUID(allNodes[i].Connections[j].NodeUID);
                            (allNodes[i].Connections[j] as OptionConnection).Option = option;
                        }
                    }
                }
            }

            // For every node: 
            // 1: Create a corresponding UI Node to represent it, and add it to the list
            // 2: Tell any of the nodes children that the node is the childs parent
            for (int i = 0; i < allNodes.Count; i++)
            {
                EditableConversationNode thisNode = allNodes[i];

                // 1
                if (thisNode is EditableSpeechNode)
                {
                    UISpeechNode uiNode = new UISpeechNode(thisNode, new Vector2(thisNode.EditorInfo.xPos, thisNode.EditorInfo.yPos));
                    uiNodes.Add(uiNode);
                }
                else if (thisNode is EditableOptionNode)
                {
                    UIOptionNode uiNode = new UIOptionNode(thisNode, new Vector2(thisNode.EditorInfo.xPos, thisNode.EditorInfo.yPos));
                    uiNodes.Add(uiNode);
                }

                // 2
                for (int j = 0; j < thisNode.Connections.Count; j++)
                {
                    if (thisNode.Connections[j] is SpeechConnection)
                    {
                        (thisNode.Connections[j] as SpeechConnection).Speech.parents.Add(thisNode);
                    }
                    else if (thisNode.Connections[j] is OptionConnection)
                    {
                        (thisNode.Connections[j] as OptionConnection).Option.parents.Add(thisNode);
                    }
                }
            }
        }



        //--------------------------------------
        // OnEnable, OnDisable, OnFocus, LostFocus, 
        // Destroy, SelectionChange, ReloadScripts
        //--------------------------------------

        private void OnEnable()
        {
            if (uiNodes == null)
                uiNodes = new List<UINode>();

            InitGUIStyles();

            UINode.OnUINodeSelected += SelectNode;
            UINode.OnUINodeDeleted += DeleteUINode;
            UISpeechNode.OnCreateOption += CreateNewOption;
            UIOptionNode.OnCreateSpeech += CreateNewSpeech;
            UISpeechNode.OnConnect += ConnectNode;

            this.name = WINDOW_NAME;
            panelWidth = START_PANEL_WIDTH;
        }

        private void InitGUIStyles()
        {
            // Panel style
            panelStyle = new GUIStyle();
            panelStyle.normal.background = DialogueEditorUtil.MakeTexture(10, 10, DialogueEditorUtil.GetEditorColor());

            // Panel title style
            panelTitleStyle = new GUIStyle();
            panelTitleStyle.alignment = TextAnchor.MiddleCenter;
            panelTitleStyle.fontStyle = FontStyle.Bold;

            // Resizer style
            resizerStyle = new GUIStyle();
            resizerStyle.normal.background = EditorGUIUtility.Load("icons/d_AvatarBlendBackground.png") as Texture2D;
        }

        private void OnDisable()
        {
            Log("Saving. Reason: Disable.");
            Save();

            UINode.OnUINodeSelected -= SelectNode;
            UINode.OnUINodeDeleted -= DeleteUINode;
            UISpeechNode.OnCreateOption -= CreateNewOption;
            UIOptionNode.OnCreateSpeech -= CreateNewSpeech;
            UISpeechNode.OnConnect -= ConnectNode;
        }

        protected void OnFocus()
        {
            // Get asset the user is selecting
            newlySelectedAsset = Selection.activeTransform;

            // If it's not null
            if (newlySelectedAsset != null)
            {
                // If its a conversation scriptable, load new asset
                if (newlySelectedAsset.GetComponent<NPCConversation>() != null)
                {
                    currentlySelectedAsset = newlySelectedAsset.GetComponent<NPCConversation>();

                    if (currentlySelectedAsset != CurrentAsset)
                    {
                        LoadNewAsset(currentlySelectedAsset);
                    }
                }
            }
        }

        protected void OnLostFocus()
        {
            bool keepOnWindow = EditorWindow.focusedWindow != null && EditorWindow.focusedWindow.titleContent.text.Equals("Dialogue Editor");

            if (CurrentAsset != null && !keepOnWindow)
            {
                Log("Saving conversation. Reason: Window Lost Focus.");
                Save();
            }
        }

        protected void OnDestroy()
        {
            Log("Saving conversation. Reason: Window closed.");
            Save();
        }

        protected void OnSelectionChange()
        {
            // Get asset the user is selecting
            newlySelectedAsset = Selection.activeTransform;

            // If it's not null
            if (newlySelectedAsset != null)
            {
                // If it's a different asset and our current asset isn't null, save our current asset
                if (currentlySelectedAsset != null && currentlySelectedAsset != newlySelectedAsset)
                {
                    Log("Saving conversation. Reason: Different asset selected");
                    Save();
                    currentlySelectedAsset = null;
                }

                // If its a conversation scriptable, load new asset
                currentlySelectedAsset = newlySelectedAsset.GetComponent<NPCConversation>();
                if (currentlySelectedAsset != null && currentlySelectedAsset != CurrentAsset)
                {
                    LoadNewAsset(currentlySelectedAsset);
                }
                else
                {
                    CurrentAsset = null;
                    Repaint();
                }
            }
            else
            {
                Log("Saving conversation. Reason: Conversation asset de-selected");
                Save();

                CurrentAsset = null;
                currentlySelectedAsset = null;
                Repaint();
            }
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            // Clear our reffrence to the CurrentAsset on script reload in order to prevent 
            // save detection overwriting the object with an empty conversation (save triggerred 
            // with no uiNodes present in window due to recompile). 
            Log("Scripts reloaded. Clearing current asset.");
            ShowWindow().CurrentAsset = null;
        }



        //--------------------------------------
        // Update
        //--------------------------------------

        private void Update()
        {
            switch (m_inputState)
            {
                case eInputState.PlacingOption:
                case eInputState.PlacingSpeech:
                    Repaint();
                    break;
            }
        }



        //--------------------------------------
        // Draw
        //--------------------------------------

        private void OnGUI()
        {
            if (CurrentAsset == null)
            {
                DrawTitleBar();
                Repaint();
                return;
            }

            // Process interactions
            ProcessInput();

            // Draw
            DrawGrid(20, 0.2f, Color.gray);
            DrawGrid(100, 0.4f, Color.gray);
            DrawConnections();
            DrawNodes();
            DrawPanel();
            DrawResizer();
            DrawTitleBar();

            if (GUI.changed)
                Repaint();
        }

        private void DrawTitleBar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Reset view", EditorStyles.toolbarButton))
            {
                Recenter();
            }
            if (GUILayout.Button("Reset panel", EditorStyles.toolbarButton))
            {
                ResetPanelSize();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Manual Save", EditorStyles.toolbarButton))
            {
                Save(true);
            }
            if (GUILayout.Button("Help", EditorStyles.toolbarButton))
            {
                Application.OpenURL(HELP_URL);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawNodes()
        {
            if (uiNodes != null)
            {
                for (int i = 0; i < uiNodes.Count; i++)
                {
                    uiNodes[i].Draw();
                }
            }
        }

        private void DrawConnections()
        {
            for (int i = 0; i < uiNodes.Count; i++)
            {
                uiNodes[i].DrawConnections();
            }

            if (m_inputState == eInputState.ConnectingNode)
            {
                // Validate check
                if (m_currentConnectingNode == null)
                {
                    m_inputState = eInputState.Regular;
                    return;
                }

                Vector2 start, end;
                start = new Vector2(
                    m_currentConnectingNode.rect.x + UIOptionNode.Width / 2,
                    m_currentConnectingNode.rect.y + UIOptionNode.Height / 2
                    );
                end = Event.current.mousePosition;

                Vector2 toOption = (start - end).normalized;
                Vector2 toSpeech = (end - start).normalized;

                Handles.DrawBezier(
                    start, end,
                    start + toSpeech * 50f,
                    end + toOption * 50f,
                    Color.black, null, 5f);

                Repaint();
            }
        }

        private void DrawGrid(float gridSpacing, float gridOpacity, Color gridColor)
        {
            int widthDivs = Mathf.CeilToInt(position.width / gridSpacing);
            int heightDivs = Mathf.CeilToInt(position.height / gridSpacing);

            Handles.BeginGUI();
            Handles.color = new Color(gridColor.r, gridColor.g, gridColor.b, gridOpacity);

            offset += dragDelta * 0.5f;
            Vector3 newOffset = new Vector3(offset.x % gridSpacing, offset.y % gridSpacing, 0);

            // Vertical lines
            for (int i = 0; i < widthDivs; i++)
            {
                Vector3 start = new Vector3(gridSpacing * i, -gridSpacing, 0) + newOffset;
                Vector3 end = new Vector3(gridSpacing * i, position.height, 0f) + newOffset;
                Handles.DrawLine(start, end);
            }

            // Horitonzal lines
            for (int j = 0; j < heightDivs; j++)
            {
                Vector3 start = new Vector3(-gridSpacing, gridSpacing * j, 0) + newOffset;
                Vector3 end = new Vector3(position.width, gridSpacing * j, 0f) + newOffset;
                Handles.DrawLine(start, end);
            }

            Handles.color = Color.white;
            Handles.EndGUI();
        }

        private Vector2 panelVerticalScroll;

        private void DrawPanel()
        {
            const int VERTICAL_PADDING = 10;

            panelRect = new Rect(position.width - panelWidth, TOOLBAR_HEIGHT, panelWidth, position.height - TOOLBAR_HEIGHT);
            if (panelStyle.normal.background == null)
                InitGUIStyles();
            GUILayout.BeginArea(panelRect, panelStyle);
            GUILayout.BeginVertical();
            panelVerticalScroll = GUILayout.BeginScrollView(panelVerticalScroll);

            GUI.SetNextControlName("CONTROL_TITLE");

            GUILayout.Space(10);

            if (CurrentlySelectedNode == null)
            {
                GUILayout.Label("Default options", panelTitleStyle);

                // Default options
                GUILayout.Label("Default name:", EditorStyles.boldLabel);
                CurrentAsset.DefaultName = EditorGUILayout.TextField(CurrentAsset.DefaultName);

                GUILayout.Label("Default Icon:", EditorStyles.boldLabel);
                CurrentAsset.DefaultSprite = (Sprite)EditorGUILayout.ObjectField(CurrentAsset.DefaultSprite, typeof(Sprite), false);

                GUILayout.Label("Default font:", EditorStyles.boldLabel);
                CurrentAsset.DefaultFont = (TMPro.TMP_FontAsset)EditorGUILayout.ObjectField(CurrentAsset.DefaultFont, typeof(TMPro.TMP_FontAsset), false);

                GUILayout.Space(VERTICAL_PADDING);

                // Parameters

                if (CurrentAsset.ParameterList == null)
                    CurrentAsset.ParameterList = new List<Parameter>();

                GUILayout.Label("Parameters", panelTitleStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Add bool"))
                {
                    CurrentAsset.ParameterList.Add(new BoolParameter("Unnamed bool"));
                }
                if (GUILayout.Button("Add int"))
                {
                    CurrentAsset.ParameterList.Add(new IntParameter("Unnamed int"));
                }
                GUILayout.EndHorizontal();

                for (int i = 0; i < CurrentAsset.ParameterList.Count; i++)
                {
                    GUILayout.BeginHorizontal();

                    float paramNameWidth = panelWidth * 0.6f;
                    CurrentAsset.ParameterList[i].ParameterName = GUILayout.TextField(
                        CurrentAsset.ParameterList[i].ParameterName, GUILayout.Width(paramNameWidth), GUILayout.ExpandWidth(false));

                    if (CurrentAsset.ParameterList[i] is BoolParameter)
                    {
                        BoolParameter param = CurrentAsset.ParameterList[i] as BoolParameter;
                        param.BoolValue = EditorGUILayout.Toggle(param.BoolValue);
                    }
                    else if (CurrentAsset.ParameterList[i] is IntParameter)
                    {
                        IntParameter param = CurrentAsset.ParameterList[i] as IntParameter;
                        param.IntValue = EditorGUILayout.IntField(param.IntValue);
                    }

                    if (GUILayout.Button("X"))
                    {
                        CurrentAsset.ParameterList.RemoveAt(i);
                        i--;
                    }

                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                bool differentNodeSelected = (m_cachedSelectedNode != CurrentlySelectedNode);
                m_cachedSelectedNode = CurrentlySelectedNode;
                if (differentNodeSelected)
                {
                    GUI.FocusControl(CONTROL_NAME);
                }

                if (CurrentlySelectedNode is UISpeechNode)
                {
                    EditableSpeechNode node = (CurrentlySelectedNode.Info as EditableSpeechNode);
                    GUILayout.Label("[" + node.ID + "] NPC Dialogue Node.", panelTitleStyle);
                    EditorGUILayout.Space();

                    GUILayout.Label("Character Name", EditorStyles.boldLabel);
                    GUI.SetNextControlName(CONTROL_NAME);
                    node.Name = GUILayout.TextField(node.Name);
                    EditorGUILayout.Space();

                    GUILayout.Label("Dialogue", EditorStyles.boldLabel);
                    node.Text = GUILayout.TextArea(node.Text);
                    EditorGUILayout.Space();

                    // Advance
                    if (node.Connections.Count > 0 && node.Connections[0] is SpeechConnection) 
                    {
                        GUILayout.Label("Auto-Advance options", EditorStyles.boldLabel);
                        node.AdvanceDialogueAutomatically = EditorGUILayout.Toggle("Automatically Advance", node.AdvanceDialogueAutomatically);
                        if (node.AdvanceDialogueAutomatically)
                        {
                            node.AutoAdvanceShouldDisplayOption = EditorGUILayout.Toggle("Display continue option", node.AutoAdvanceShouldDisplayOption);
                            node.TimeUntilAdvance = EditorGUILayout.FloatField("Dialogue Time", node.TimeUntilAdvance);
                            if (node.TimeUntilAdvance < 0.1f)
                                node.TimeUntilAdvance = 0.1f;
                        }
                        EditorGUILayout.Space();
                    }

                    GUILayout.Label("Icon", EditorStyles.boldLabel);
                    node.Icon = (Sprite)EditorGUILayout.ObjectField(node.Icon, typeof(Sprite), false, GUILayout.ExpandWidth(true));
                    EditorGUILayout.Space();

                    GUILayout.Label("Audio Options", EditorStyles.boldLabel);
                    GUILayout.Label("Audio");
                    node.Audio = (AudioClip)EditorGUILayout.ObjectField(node.Audio, typeof(AudioClip), false);

                    GUILayout.Label("Audio Volume");
                    node.Volume = EditorGUILayout.Slider(node.Volume, 0, 1);
                    EditorGUILayout.Space();

                    GUILayout.Label("TMP Font", EditorStyles.boldLabel);
                    node.TMPFont = (TMPro.TMP_FontAsset)EditorGUILayout.ObjectField(node.TMPFont, typeof(TMPro.TMP_FontAsset), false);
                    EditorGUILayout.Space();

                    // Events
                    {
                        NodeEventHolder NodeEvent = CurrentAsset.GetNodeData(node.ID);
                        if (differentNodeSelected)
                        {
                            CurrentAsset.Event = NodeEvent.Event;
                        }

                        if (NodeEvent != null && NodeEvent.Event != null)
                        {
                            // Load the object and property of the node
                            SerializedObject o = new SerializedObject(NodeEvent);
                            SerializedProperty p = o.FindProperty("Event");

                            // Load the dummy event
                            SerializedObject o2 = new SerializedObject(CurrentAsset);
                            SerializedProperty p2 = o2.FindProperty("Event");

                            // Draw dummy event
                            GUILayout.Label("Events:", EditorStyles.boldLabel);
                            EditorGUILayout.PropertyField(p2);

                            // Apply changes to dummy
                            o2.ApplyModifiedProperties();

                            // Copy dummy changes onto the nodes event
                            p = p2;
                            o.ApplyModifiedProperties();
                        }
                    }
                }
                else if (CurrentlySelectedNode is UIOptionNode)
                {
                    EditableOptionNode node = (CurrentlySelectedNode.Info as EditableOptionNode);
                    GUILayout.Label("[" + node.ID + "] Option Node.", panelTitleStyle);                   

                    GUILayout.Label("Option text:", EditorStyles.boldLabel);
                    node.Text = GUILayout.TextArea(node.Text);

                    GUILayout.Label("TMP Font", EditorStyles.boldLabel);
                    node.TMPFont = (TMPro.TMP_FontAsset)EditorGUILayout.ObjectField(node.TMPFont, typeof(TMPro.TMP_FontAsset), false);

                    // Conditions
                    GUILayout.Space(VERTICAL_PADDING);
                    GUILayout.Label("Conditions", panelTitleStyle);

                    /*
                    // Add condition
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Add condition"))
                    {
                        GenericMenu rightClickMenu = new GenericMenu();

                        for (int i = 0; i < this.CurrentAsset.ParameterList.Count; i++)
                        {
                            if (this.CurrentAsset.ParameterList[i] is IntParameter)
                            {
                                IntParameter intParam = CurrentAsset.ParameterList[i] as IntParameter;
                                rightClickMenu.AddItem(new GUIContent(intParam.ParameterName), false, delegate
                                {
                                    node.Conditions.Add(new IntCondition(intParam.ParameterName));
                                });
                            }
                            else if (this.CurrentAsset.ParameterList[i] is BoolParameter)
                            {
                                BoolParameter boolParam = CurrentAsset.ParameterList[i] as BoolParameter;
                                rightClickMenu.AddItem(new GUIContent(boolParam.ParameterName), false, delegate
                                {
                                    node.Conditions.Add(new BoolCondition(boolParam.ParameterName));
                                });
                            }
                        }

                        rightClickMenu.ShowAsContext();
                    }
                    GUILayout.EndHorizontal();


                    // Validate conditions
                    for (int i = 0; i < node.Conditions.Count; i++)
                    {
                        if (CurrentAsset.GetParameter(node.Conditions[i].ParameterName) == null)
                        {
                            node.Conditions.RemoveAt(i);
                            i--;
                        }
                    }


                    // Draw conditions
                    float conditionNameWidth = panelWidth * 0.4f;
                    for (int i = 0; i < node.Conditions.Count; i++)
                    {
                        GUILayout.BeginHorizontal();

                        string name = node.Conditions[i].ParameterName;
                        GUILayout.Label(name, EditorStyles.boldLabel, GUILayout.MinWidth(conditionNameWidth), GUILayout.MaxWidth(conditionNameWidth));

                        if (node.Conditions[i] is IntCondition)
                        {
                            IntCondition intCond = node.Conditions[i] as IntCondition;

                            intCond.CheckType = (IntCondition.eCheckType)EditorGUILayout.EnumPopup(intCond.CheckType);
                            intCond.RequiredValue = EditorGUILayout.IntField(intCond.RequiredValue);

                        }
                        else if (node.Conditions[i] is BoolCondition)
                        {
                            BoolCondition boolCond = node.Conditions[i] as BoolCondition;

                            boolCond.CheckType = (BoolCondition.eCheckType)EditorGUILayout.EnumPopup(boolCond.CheckType);
                            boolCond.RequiredValue = EditorGUILayout.Toggle(boolCond.RequiredValue);
                        }

                        if (GUILayout.Button("X"))
                        {
                            node.Conditions.RemoveAt(i);
                            i--;
                        }

                        GUILayout.EndHorizontal();
                    }
                    */
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawResizer()
        {
            panelResizerRect = new Rect(
                position.width - panelWidth - 2,
                0,
                5,
                (position.height) - TOOLBAR_HEIGHT);
            GUILayout.BeginArea(new Rect(panelResizerRect.position, new Vector2(2, position.height)), resizerStyle);
            GUILayout.EndArea();
        }




        //--------------------------------------
        // Input
        //--------------------------------------

        private void ProcessInput()
        {
            Event e = Event.current;

            switch (m_inputState)
            {
                case eInputState.Regular:
                    bool inPanel = panelRect.Contains(e.mousePosition) || e.mousePosition.y < TOOLBAR_HEIGHT;
                    ProcessNodeEvents(e, inPanel);
                    ProcessEvents(e);
                    break;

                case eInputState.draggingPanel:
                    panelWidth = (position.width - e.mousePosition.x);
                    if (panelWidth < MIN_PANEL_WIDTH)
                        panelWidth = MIN_PANEL_WIDTH;

                    if (e.type == EventType.MouseUp && e.button == 0)
                    {
                        m_inputState = eInputState.Regular;
                        e.Use();
                    }
                    Repaint();
                    break;

                case eInputState.PlacingOption:
                    m_currentPlacingNode.SetPosition(e.mousePosition);

                    // Left click
                    if (e.type == EventType.MouseDown && e.button == 0)
                    {
                        // Place the option
                        SelectNode(m_currentPlacingNode, true);
                        m_inputState = eInputState.Regular;
                        Repaint();
                        e.Use();
                    }
                    break;

                case eInputState.PlacingSpeech:
                    m_currentPlacingNode.SetPosition(e.mousePosition);

                    // Left click
                    if (e.type == EventType.MouseDown && e.button == 0)
                    {
                        // Place the option
                        SelectNode(m_currentPlacingNode, true);
                        m_inputState = eInputState.Regular;
                        Repaint();
                        e.Use();
                    }
                    break;

                case eInputState.ConnectingNode:
                    // Click.
                    if (e.type == EventType.MouseDown && e.button == 0)
                    {
                        // Loop through each node
                        for (int i = 0; i < uiNodes.Count; i++)
                        {
                            if (uiNodes[i] == m_currentConnectingNode)
                                continue;

                            // Clicked on node
                            if (uiNodes[i].rect.Contains(e.mousePosition))
                            {
                                UINode parent = m_currentConnectingNode;
                                UINode target = uiNodes[i];

                                // Connecting node->Option
                                if (target is UIOptionNode)
                                {
                                    UIOptionNode targetOption = target as UIOptionNode;

                                    // Only speech -> option is valid
                                    if (parent is UISpeechNode)
                                    {
                                        (parent as UISpeechNode).SpeechNode.AddOption(targetOption.OptionNode);
                                    }
                                }

                                // Connectingnode->Speech
                                else if (target is UISpeechNode)
                                {
                                    UISpeechNode targetSpeech = target as UISpeechNode;

                                    // Connect
                                    if (parent is UISpeechNode)
                                    {
                                        (parent as UISpeechNode).SpeechNode.AddSpeech(targetSpeech.SpeechNode);
                                    }
                                    else if (parent is UIOptionNode)
                                    {
                                        (parent as UIOptionNode).OptionNode.AddSpeech(targetSpeech.SpeechNode);
                                    }
                                }

                                m_inputState = eInputState.Regular;
                                e.Use();
                                break;
                            }
                        }
                    }

                    // Esc
                    if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                    {
                        m_inputState = eInputState.Regular;
                    }
                    break;
            }
        }

        private void ProcessEvents(Event e)
        {
            dragDelta = Vector2.zero;

            switch (e.type)
            {
                case EventType.MouseDown:
                    // Left click
                    if (e.button == 0)
                    {
                        if (panelRect.Contains(e.mousePosition))
                        {
                            clickInBox = true;
                        }
                        else if (InPanelDrag(e.mousePosition))
                        {
                            clickInBox = true;
                            m_inputState = eInputState.draggingPanel;
                        }
                        else if (e.mousePosition.y > TOOLBAR_HEIGHT)
                        {
                            clickInBox = false;
                            if (!DialogueEditorWindow.NodeClickedOnThisUpdate)
                            {
                                UnselectNode();
                                e.Use();
                            }
                        }
                    }
                    // Right click
                    else if (e.button == 1)
                    {
                        if (DialogueEditorUtil.IsPointerNearConnection(uiNodes, e.mousePosition, out m_connectionDeleteParent, out m_connectionDeleteChild))
                        {
                            GenericMenu rightClickMenu = new GenericMenu();
                            rightClickMenu.AddItem(new GUIContent("Delete connection"), false, DeleteConnection);
                            rightClickMenu.ShowAsContext();
                        }
                    }

                    if (e.button == 0 || e.button == 2)
                        dragging = true;
                    else
                        dragging = false;
                    break;

                case EventType.MouseDrag:
                    if (dragging && (e.button == 0 || e.button == 2) && !clickInBox && !IsANodeSelected())
                    {
                        OnDrag(e.delta);
                    }
                    break;

                case EventType.MouseUp:
                    dragging = false;
                    break;
            }
        }

        private void ProcessNodeEvents(Event e, bool inPanel)
        {
            if (uiNodes != null)
            {
                NodeClickedOnThisUpdate = false;

                for (int i = 0; i < uiNodes.Count; i++)
                {
                    bool guiChanged = uiNodes[i].ProcessEvents(e, inPanel);
                    if (guiChanged)
                        GUI.changed = true;
                }
            }
        }

        private void OnDrag(Vector2 delta)
        {
            dragDelta = delta;

            if (uiNodes != null)
            {
                for (int i = 0; i < uiNodes.Count; i++)
                {
                    uiNodes[i].Drag(delta);
                }
            }

            GUI.changed = true;
        }




        //--------------------------------------
        // Event listeners
        //--------------------------------------

        /* -- Creating Nodes -- */

        public void CreateNewOption(UISpeechNode speechUI)
        {
            // Create new option, the argument speech is the options parent
            EditableOptionNode newOption = new EditableOptionNode();
            newOption.ID = CurrentAsset.CurrentIDCounter++;

            // Give the speech it's default values
            newOption.TMPFont = CurrentAsset.DefaultFont;

            // Add the option to the speechs' list of options
            speechUI.SpeechNode.AddOption(newOption);

            // Create a new UI object to represent the new option
            UIOptionNode ui = new UIOptionNode(newOption, Vector2.zero);
            uiNodes.Add(ui);

            // Set the input state appropriately
            m_inputState = eInputState.PlacingOption;
            m_currentPlacingNode = ui;
        }


        public void CreateNewSpeech(UINode node)
        {
            // Create new speech, the argument option is the speechs parent
            EditableSpeechNode newSpeech = new EditableSpeechNode();
            newSpeech.ID = CurrentAsset.CurrentIDCounter++;

            // Give the speech it's default values
            newSpeech.Name = CurrentAsset.DefaultName;
            newSpeech.Icon = CurrentAsset.DefaultSprite;
            newSpeech.TMPFont = CurrentAsset.DefaultFont;

            // Set this new speech as the options child
            if (node is UIOptionNode)
                (node as UIOptionNode).OptionNode.AddSpeech(newSpeech);
            else if (node is UISpeechNode)
                (node as UISpeechNode).SpeechNode.AddSpeech(newSpeech);

            // Create a new UI object to represent the new speech
            UISpeechNode ui = new UISpeechNode(newSpeech, Vector2.zero);
            uiNodes.Add(ui);

            // Set the input state appropriately
            m_inputState = eInputState.PlacingSpeech;
            m_currentPlacingNode = ui;
        }


        /* -- Connecting Nodes -- */

        public void ConnectNode(UINode option)
        {
            // The option if what we are connecting
            m_currentConnectingNode = option;

            // Set the input state appropriately
            m_inputState = eInputState.ConnectingNode;
        }


        /* -- Deleting Nodes -- */

        public void DeleteUINode(UINode node)
        {
            if (ConversationRoot == node.Info)
            {
                Log("Cannot delete root node.");
                return;
            }

            // Delete tree/internal objects
            node.Info.RemoveSelfFromTree();

            // Delete the EventHolder script if it's an speech node
            CurrentAsset.DeleteDataForNode(node.Info.ID);

            // Delete the UI classes
            uiNodes.Remove(node);
            node = null;

            // "Unselect" what we were looking at.
            CurrentlySelectedNode = null;
        }

        /* -- Deleting connection -- */

        public void DeleteConnection()
        {
            if (m_connectionDeleteParent != null && m_connectionDeleteChild != null)
            {
                // Remove child->parent relationship
                m_connectionDeleteChild.parents.Remove(m_connectionDeleteParent);

                // Remove parent->child relationship
                // Look through each connection the parent has
                // Remove the connection if it points to the child
                for (int i = 0; i < m_connectionDeleteParent.Connections.Count; i++)
                {
                    Connection connection = m_connectionDeleteParent.Connections[i];

                    if (connection is SpeechConnection && (connection as SpeechConnection).Speech == m_connectionDeleteChild)
                    {
                        m_connectionDeleteParent.Connections.RemoveAt(i);
                        i--;
                    }
                    else if (connection is OptionConnection && (connection as OptionConnection).Option == m_connectionDeleteChild)
                    {
                        m_connectionDeleteParent.Connections.RemoveAt(i);
                        i--;
                    }
                }
            }

            m_connectionDeleteParent = null;
            m_connectionDeleteChild = null;
        }




        //--------------------------------------
        // Util
        //--------------------------------------

        private void SelectNode(UINode node, bool selected)
        {
            if (selected)
            {
                if (CurrentlySelectedNode != null)
                    CurrentlySelectedNode.SetSelected(false);

                CurrentlySelectedNode = node;
                CurrentlySelectedNode.SetSelected(true);
            }
            else
            {
                node.SetSelected(false);
                CurrentlySelectedNode = null;
            }
        }

        private void UnselectNode()
        {
            if (CurrentlySelectedNode != null)
                CurrentlySelectedNode.SetSelected(false);
            CurrentlySelectedNode = null;
        }

        private bool IsANodeSelected()
        {
            if (uiNodes != null)
            {
                for (int i = 0; i < uiNodes.Count; i++)
                {
                    if (uiNodes[i].isSelected) return true;
                }
            }
            return false;
        }

        private bool InPanelDrag(Vector2 pos)
        {
            return (
                pos.x > panelResizerRect.x - panelResizerRect.width - PANEL_RESIZER_PADDING &&
                pos.x < panelResizerRect.x + panelResizerRect.width + PANEL_RESIZER_PADDING &&
                pos.y > panelResizerRect.y &&
                panelResizerRect.y < panelResizerRect.y + panelResizerRect.height);        
        }

        private static void Log(string str)
        {
#if DIALOGUE_DEBUG
            Debug.Log("[DialogueEditor]: " + str);
#endif
        }




        //--------------------------------------
        // User / Save functionality
        //--------------------------------------

        private void Recenter()
        {
            if (ConversationRoot == null) { return; }

            // Calc delta to move head to (middle, 0) and then apply this to all nodes
            Vector2 target = new Vector2((position.width / 2) - (UISpeechNode.Width / 2) - (panelWidth / 2), TOOLBAR_HEIGHT + 5);
            Vector2 delta = target - new Vector2(ConversationRoot.EditorInfo.xPos, ConversationRoot.EditorInfo.yPos);
            for (int i = 0; i < uiNodes.Count; i++)
            {
                uiNodes[i].Drag(delta);
            }
            Repaint();
        }

        private void ResetPanelSize()
        {
            panelWidth = START_PANEL_WIDTH;
        }

        private void Save(bool manual = false)
        {
            if (CurrentAsset != null)
            {
                EditableConversation conversation = new EditableConversation();

                // Prepare each node for serialization
                for (int i = 0; i < uiNodes.Count; i++)
                {
                    uiNodes[i].Info.SerializeAssetData(CurrentAsset);
                }

                // Now that each node has been prepared for serialization: 
                // - Register the UIDs of their parents/children
                // - Add it to the conversation
                for (int i = 0; i < uiNodes.Count; i++)
                {
                    uiNodes[i].Info.RegisterUIDs();

                    if (uiNodes[i] is UISpeechNode)
                    {
                        conversation.SpeechNodes.Add((uiNodes[i] as UISpeechNode).SpeechNode);
                    }
                    else if (uiNodes[i] is UIOptionNode)
                    {
                        conversation.Options.Add((uiNodes[i] as UIOptionNode).OptionNode);
                    }
                }

                // Serialize
                CurrentAsset.Serialize(conversation);

                // Null / clear everything. We aren't pointing to it anymore. 
                if (!manual)
                {
                    CurrentAsset = null;
                    while (uiNodes.Count != 0)
                        uiNodes.RemoveAt(0);
                    CurrentlySelectedNode = null;
                }

#if UNITY_EDITOR
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
#endif
            }
        }
    }
}