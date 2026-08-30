namespace VictoryTool.Desktop.Localization;

public sealed record UiText(
    string Project,
    string Characters,
    string Batch,
    string Export,
    string Research,
    string LoadDump,
    string DumpPath,
    string SearchCharacters,
    string Roster,
    string Comparison,
    string Inspector,
    string CloneCharacter,
    string Original,
    string Custom,
    string CharacterBatch,
    string AddPackage,
    string PackagePath,
    string NoCharacterSelected,
    string NoDraft,
    string PreviewExport,
    string OutputPath,
    string Diagnostics,
    string ImmutableDump,
    string Autosave,
    string WelcomeTitle,
    string WelcomeBody)
{
    public static UiText English { get; } = new(
        "Project", "Characters", "Batch", "Export", "Research",
        "Load dump", "Game dump root", "Search by name or ID", "Roster",
        "Comparison", "Inspector", "Clone as .vrchara", "Original", "Custom",
        "Character batch", "Add package", "Path to a .vrchara package",
        "Select a character from the roster.", "Clone a character to start editing.",
        "Preview export", "Output folder", "Diagnostics", "Immutable dump", "Autosave",
        "Connect a game dump", "VictoryTool needs an extracted dump containing common/gamedata. The source is always read-only.");

    public static UiText Spanish { get; } = new(
        "Proyecto", "Personajes", "Lote", "Exportar", "Investigación",
        "Cargar dump", "Raíz del dump del juego", "Buscar por nombre o ID", "Plantilla",
        "Comparación", "Inspector", "Clonar como .vrchara", "Original", "Personalizado",
        "Lote de personajes", "Añadir paquete", "Ruta a un paquete .vrchara",
        "Selecciona un personaje de la plantilla.", "Clona un personaje para empezar a editar.",
        "Previsualizar exportación", "Carpeta de salida", "Diagnósticos", "Dump inmutable", "Autoguardado",
        "Conecta un dump del juego", "VictoryTool necesita un dump extraído que contenga common/gamedata. La fuente siempre es de solo lectura.");

    public static UiText German { get; } = new(
        "Projekt", "Personen", "Stapel", "Export", "Forschung",
        "Dump laden", "Spiel-Dump-Stammordner", "Nach Name oder ID suchen", "Kader",
        "Vergleich", "Inspektor", "Als .vrchara klonen", "Original", "Benutzerdefiniert",
        "Charakterstapel", "Paket hinzufügen", "Pfad zu einem .vrchara-Paket",
        "Wähle einen Charakter aus dem Kader.", "Klone einen Charakter, um ihn zu bearbeiten.",
        "Export prüfen", "Ausgabeordner", "Diagnosen", "Unveränderlicher Dump", "Automatisch speichern",
        "Spiel-Dump verbinden", "VictoryTool benötigt einen extrahierten Dump mit common/gamedata. Die Quelle bleibt schreibgeschützt.");

    public static UiText French { get; } = new(
        "Projet", "Personnages", "Lot", "Exporter", "Recherche",
        "Charger le dump", "Racine du dump", "Rechercher par nom ou ID", "Effectif",
        "Comparaison", "Inspecteur", "Cloner en .vrchara", "Original", "Personnalisé",
        "Lot de personnages", "Ajouter un paquet", "Chemin d’un paquet .vrchara",
        "Sélectionnez un personnage dans l’effectif.", "Clonez un personnage pour commencer la modification.",
        "Prévisualiser l’export", "Dossier de sortie", "Diagnostics", "Dump immuable", "Sauvegarde auto",
        "Connecter un dump du jeu", "VictoryTool nécessite un dump extrait contenant common/gamedata. La source reste en lecture seule.");

    public static UiText Italian { get; } = new(
        "Progetto", "Personaggi", "Lotto", "Esporta", "Ricerca",
        "Carica dump", "Radice del dump", "Cerca per nome o ID", "Rosa",
        "Confronto", "Inspector", "Clona come .vrchara", "Originale", "Personalizzato",
        "Lotto personaggi", "Aggiungi pacchetto", "Percorso di un pacchetto .vrchara",
        "Seleziona un personaggio dalla rosa.", "Clona un personaggio per iniziare a modificarlo.",
        "Anteprima esportazione", "Cartella di output", "Diagnostica", "Dump immutabile", "Salvataggio automatico",
        "Collega un dump del gioco", "VictoryTool richiede un dump estratto contenente common/gamedata. La sorgente resta in sola lettura.");

    public static UiText Japanese { get; } = new(
        "プロジェクト", "キャラクター", "バッチ", "エクスポート", "調査",
        "ダンプを読み込む", "ゲームダンプのルート", "名前またはIDで検索", "選手一覧",
        "比較", "インスペクター", ".vrcharaとして複製", "オリジナル", "カスタム",
        "キャラクターバッチ", "パッケージを追加", ".vrcharaパッケージのパス",
        "一覧からキャラクターを選択してください。", "編集するにはキャラクターを複製してください。",
        "エクスポートを確認", "出力フォルダー", "診断", "読み取り専用ダンプ", "自動保存",
        "ゲームダンプを接続", "VictoryToolにはcommon/gamedataを含む展開済みダンプが必要です。元データは常に読み取り専用です。");

    public static UiText Portuguese { get; } = new(
        "Projeto", "Personagens", "Lote", "Exportar", "Pesquisa",
        "Carregar dump", "Raiz do dump do jogo", "Pesquisar por nome ou ID", "Plantel",
        "Comparação", "Inspetor", "Clonar como .vrchara", "Original", "Personalizado",
        "Lote de personagens", "Adicionar pacote", "Caminho de um pacote .vrchara",
        "Selecione um personagem do plantel.", "Clone um personagem para começar a editar.",
        "Pré-visualizar exportação", "Pasta de saída", "Diagnósticos", "Dump imutável", "Gravação automática",
        "Ligar um dump do jogo", "VictoryTool requer um dump extraído com common/gamedata. A origem permanece apenas de leitura.");

    public static UiText SimplifiedChinese { get; } = new(
        "项目", "角色", "批处理", "导出", "研究",
        "加载转储", "游戏转储根目录", "按名称或ID搜索", "角色列表",
        "比较", "检查器", "克隆为.vrchara", "原版", "自定义",
        "角色批处理", "添加包", ".vrchara包路径",
        "请从列表中选择角色。", "克隆角色后即可开始编辑。",
        "预览导出", "输出文件夹", "诊断", "只读转储", "自动保存",
        "连接游戏转储", "VictoryTool需要包含common/gamedata的已提取转储。源数据始终为只读。");

    public static UiText TraditionalChinese { get; } = new(
        "專案", "角色", "批次", "匯出", "研究",
        "載入傾印", "遊戲傾印根目錄", "依名稱或ID搜尋", "角色清單",
        "比較", "檢查器", "複製為.vrchara", "原版", "自訂",
        "角色批次", "新增套件", ".vrchara套件路徑",
        "請從清單選擇角色。", "複製角色後即可開始編輯。",
        "預覽匯出", "輸出資料夾", "診斷", "唯讀傾印", "自動儲存",
        "連接遊戲傾印", "VictoryTool需要包含common/gamedata的已解壓傾印。來源資料永遠保持唯讀。");

    private static IReadOnlyDictionary<string, UiText> Locales { get; } =
        new Dictionary<string, UiText>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = German,
            ["en"] = English,
            ["es"] = Spanish,
            ["fr"] = French,
            ["it"] = Italian,
            ["ja"] = Japanese,
            ["pt"] = Portuguese,
            ["zh_hans"] = SimplifiedChinese,
            ["zh_hant"] = TraditionalChinese,
        };

    public static UiText ForLocale(string locale) =>
        Locales.TryGetValue(locale, out var text) ? text : English;
}
