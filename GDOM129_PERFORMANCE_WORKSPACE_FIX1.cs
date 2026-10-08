#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Xml.Linq;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.SuperDomColumns;
#endregion

namespace NinjaTrader.NinjaScript.AddOns.GuardianDOM
{
    // GDOM112 - paleta mais fechada estilo DOM antigo + divisórias horizontais fixas.

    public static class GuardianDepthBridge
    {
        private static readonly object sync = new object();
        private static readonly Dictionary<string, Dictionary<double, long>> bids =
            new Dictionary<string, Dictionary<double, long>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<double, long>> asks =
            new Dictionary<string, Dictionary<double, long>>(StringComparer.OrdinalIgnoreCase);

        public static void Publish(string instrumentName,
            Dictionary<double, long> bidSnapshot,
            Dictionary<double, long> askSnapshot)
        {
            if (string.IsNullOrEmpty(instrumentName))
                return;

            lock (sync)
            {
                bids[instrumentName] = new Dictionary<double, long>(bidSnapshot);
                asks[instrumentName] = new Dictionary<double, long>(askSnapshot);
            }
        }

        public static bool TryGet(string instrumentName,
            out Dictionary<double, long> bidSnapshot,
            out Dictionary<double, long> askSnapshot)
        {
            bidSnapshot = new Dictionary<double, long>();
            askSnapshot = new Dictionary<double, long>();

            if (string.IsNullOrEmpty(instrumentName))
                return false;

            lock (sync)
            {
                Dictionary<double, long> b;
                Dictionary<double, long> a;

                if (!bids.TryGetValue(instrumentName, out b) ||
                    !asks.TryGetValue(instrumentName, out a))
                    return false;

                bidSnapshot = new Dictionary<double, long>(b);
                askSnapshot = new Dictionary<double, long>(a);
                return true;
            }
        }
    }

    public class GuardianDomAddOn : AddOnBase
    {
        private NTMenuItem guardianDomMenuItem;

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter controlCenter = window as ControlCenter;
            if (controlCenter == null)
                return;

            guardianDomMenuItem = new NTMenuItem
            {
                Header = "Guardian DOM",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            guardianDomMenuItem.Click += GuardianDomMenuItem_Click;

            NTMenuItem newMenu =
                controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;

            if (newMenu != null)
                newMenu.Items.Add(guardianDomMenuItem);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            ControlCenter controlCenter = window as ControlCenter;
            if (controlCenter == null)
                return;

            if (guardianDomMenuItem != null)
            {
                guardianDomMenuItem.Click -= GuardianDomMenuItem_Click;

                NTMenuItem newMenu =
                    controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;

                if (newMenu != null)
                    newMenu.Items.Remove(guardianDomMenuItem);

                guardianDomMenuItem = null;
            }
        }

        private void GuardianDomMenuItem_Click(object sender, RoutedEventArgs e)
        {
            GuardianDomWindow window = new GuardianDomWindow();
            window.Show();
        }
    }

    public class GuardianDomWindow : NTWindow, IWorkspacePersistence
    {
        private InstrumentSelector instrumentSelector;
        private AccountSelector accountSelector;
        private QuantityUpDown quantitySelector;

        // V0.9.9.89 - período do fluxo exibido no DOM.
        private ComboBox periodSelector;
        private int selectedPeriodMinutes = 30;
        private readonly Queue<TimedFlowTrade> timedFlowTrades = new Queue<TimedFlowTrade>();

        private sealed class TimedFlowTrade
        {
            public DateTime Time;
            public double Price;
            public long Volume;
            public int Side;
        }

        private NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector atmStrategySelector;
        private System.Windows.Threading.DispatcherTimer atmDiagTimer;
        private NinjaTrader.NinjaScript.AtmStrategy lastAutoQtyAtm;
        // V0.9.9.84 - retry controlado para restaurar o ultimo ativo somente depois que o seletor estiver pronto.
        private DispatcherTimer lastInstrumentRestoreTimer;
        private int lastInstrumentRestoreAttempts = 0;
        // V0.9.9.86 - exige estabilidade por varios ciclos porque o InstrumentSelector
        // pode aceitar o ativo e depois ser resetado para "Selecionar" durante a inicializacao interna.
        private int lastInstrumentRestoreStableTicks = 0;
        private static string lastInstrumentSessionName = string.Empty;
        private string startupInstrumentName = string.Empty;

        private Instrument currentInstrument;

        // V0.9.9.94 - vínculo de ativo entre janelas Guardian DOM.
        // Janelas com a mesma cor acompanham a troca de ativo umas das outras.
        private static readonly object guardianLinkSync = new object();
        private static readonly List<GuardianDomWindow> guardianLinkWindows = new List<GuardianDomWindow>();
        private string instrumentLinkGroup = "Nenhum";
        private bool receivingLinkedInstrument;

        // V0.9.9.95 - botão visual de vínculo ao lado do Minimizar.
        private Button titleInstrumentLinkButton;
        private Button titleDuplicateWindowButton;
        private Popup titleInstrumentLinkPopup;
        private bool titleButtonsInstalled;
        private MarketData marketData;
        private BarsRequest historicalProfileRequest;
        private BarsRequest historicalFlowRequest;
        private DispatcherTimer depthRefreshTimer;
        // GDOM109: atualização prioritária de BID / ASK / LAST sem redesenhar a ladder inteira.
        private DispatcherTimer fastPriceRefreshTimer;

        // GDOM108 PERFORMANCE - market data is buffered off the UI thread and
        // consolidated by the existing refresh timer. This avoids one Dispatcher
        // operation for every market-data event when several DOMs are open.
        private readonly object pendingMarketSync = new object();
        private readonly Queue<PendingMarketEvent> pendingMarketEvents = new Queue<PendingMarketEvent>();
        private struct PendingMarketEvent
        {
            public MarketDataType Type;
            public double Price;
            public long Volume;
            public double Bid;
            public double Ask;
        }

        // Fluxo negociado por preço. Não usa SuperDom.Rows nem MarketDepth.
        // buyFlow = negócios classificados no ASK (agressão compradora).
        // sellFlow = negócios classificados no BID (agressão vendedora).
        private readonly Dictionary<double, long> buyFlow = new Dictionary<double, long>();
        private readonly Dictionary<double, long> sellFlow = new Dictionary<double, long>();
        private int lastAggressorSide; // +1 compra, -1 venda, 0 desconhecido
        private long flowTrades;

        // Perfil diário independente mantido pelo próprio GuardianDOM.
        private readonly Dictionary<double, long> dailyVolume = new Dictionary<double, long>();
        // Standalone: estado interno do perfil (sem dependencia externa).
        private long lastVolumeBridgeVersion = -1;
        private long maxDailyVolume;
        private double profilePoc = double.NaN;
        private double profileVah = double.NaN;
        private double profileVal = double.NaN;

        // V0.9.9.23 - diagnóstico do histórico recebido pelo BarsRequest.
        private DateTime profileRequestedFrom = DateTime.MinValue;
        private DateTime profileRequestedTo = DateTime.MinValue;
        private DateTime profileFirstBarTime = DateTime.MinValue;
        private DateTime profileLastBarTime = DateTime.MinValue;
        private double profileHistoryMinPrice = double.NaN;
        private double profileHistoryMaxPrice = double.NaN;
        private long profileHistoryTotalVolume = 0;
        private int profileHistoryLevels = 0;
        private int profileHistoryBars = 0;

        // V0.9.9.23 - negócios ao vivo recebidos enquanto o histórico está carregando.
        private bool historicalProfileLoading = false;
        private readonly Dictionary<double, long> liveVolumeDuringHistoryLoad =
            new Dictionary<double, long>();

        // V0.9.0: apenas pré-visualização local; nenhuma ordem é enviada.
        private double previewOrderPrice = double.NaN;
        private int previewOrderQuantity = 0;
        private string previewOrderSide = string.Empty;
        private string previewOrderType = string.Empty;

        private TextBlock lastValue;
        private TextBlock bidValue;
        private TextBlock askValue;
        private TextBlock connectionStatus;
        private TextBlock profileStatus;
        private TextBlock aggressionBalanceStatus;
        private TextBlock orderStateStatus;
        private Button sendPreviewButton;
        private Button cancelOrderButton;
        private Button marketBuyButton;
        private Button marketSellButton;
        private TextBlock marketPnlValue;
        // V0.9.9.72 - 0=moeda, 1=ticks, 2=pontos. Clique no PnL alterna o modo.
        private int marketPnlDisplayMode = 0;
        private Button flattenButton;
        private Order guardianSubmittedOrder;
        private Account guardianOrderAccount;
        // V0.9.9.54 - o clique em cancelar apenas SOLICITA o cancelamento.
        // A referência/estado só é liberada após confirmação terminal do NinjaTrader.
        private bool guardianCancelRequested = false;
        // V0.9.9.56 - nome estável compatível com StartAtmStrategy; confirmação real vem de OrderUpdate.
        // V0.9.9.55 - nome único por envio para impedir que ordens antigas do GuardianDOM
        // sejam confundidas com a entrada atual durante tracking/cancelamento.
        private string guardianEntrySignalName = string.Empty;
        private string guardianOrderStatus = "SEM ORDEM";
        private bool beMonitorActive = false;
        private bool be20Detected = false;
        private bool be1ChangeSent = false;
        private bool be40Detected = false;
        private double beEntryFillPrice = double.NaN;
        private int beEntryDirection = 0;
        private Order guardianStopOrder;
        private Order guardianTargetOrder;
        private bool bracketSubmittedForEntry = false;
        private const int ProtectiveStopTicks = 40;
        private const int ProtectiveTargetTicks = 50;

        private Grid ladder;
        // V0.9.9.74 - fundo uniforme e personalizável da ladder.
        private Color ladderBaseColor = Color.FromRgb(55, 55, 58);
        private Brush LadderBaseBrush { get { return new SolidColorBrush(ladderBaseColor); } }
        private TextBlock[] bidCells;
        private TextBlock[] priceCells;
        private TextBlock[] askCells;
        private Border[] bidBorders;
        private Border[] priceBorders;
        private Border[] askBorders;
        private Grid[] buyFlowGrids;
        private Grid[] sellFlowGrids;
        private Border[] buyFlowBars;
        private Border[] sellFlowBars;
        private Border[] buyImbalanceBorders;
        private Border[] sellImbalanceBorders;
        private TextBlock[] volumeCells;
        private Border[] volumeBorders;
        private Grid[] volumeGrids;
        private Border[] volumeBars;

        // V0.9.9.24 - Delta por nível = agressão compradora - agressão vendedora.
        private TextBlock[] deltaCells;
        private TextBlock[] deltaNegativeCells;
        private TextBlock[] deltaPositiveCells;
        private Border[] deltaBorders;
        private Grid[] deltaGrids;
        private Border[] deltaPositiveBars;
        private Border[] deltaNegativeBars;

        private double lastPrice;
        private double bidPrice;
        private double askPrice;

        private const int LadderRows = 41;
        private const int CenterRow = 20;
        private const double LadderRowHeight = 17.0;
        private const int PanelHiddenOuterRows = 3;

        // Navegação manual da ladder. Zero = acompanha o mercado.
        private int ladderOffsetTicks = 0;
        private bool ladderManualNavigation = false;

        // V0.9.9.82 - imbalance DIAGONAL 3x por BORDA na célula de agressão.
        // Regra inicial conservadora: lado dominante >= 3x o lado oposto
        // e pelo menos 20 contratos no lado dominante.
        private bool imbalanceEnabled = true;
        private const double ImbalanceRatio = 3.0;
        private const long ImbalanceMinVolume = 20;

        // V0.9.9.43 - centro visual persistente da ladder.
        // Em AUTO, só é deslocado quando o mercado chega perto das bordas.
        private double ladderDisplayCenter = double.NaN;
        private const int AutoCenterEdgeRows = 5;

        // V0.9.9.94 - integra "Janela Duplicar" ao menu nativo da barra de titulo.
        private const int GuardianDuplicateWindowCommand = 0x1F10;
        private HwndSource guardianHwndSource;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DrawMenuBar(IntPtr hWnd);

        private const uint MF_BYPOSITION = 0x00000400;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint MF_STRING = 0x00000000;

        // GDOM129: preferências de visualização exclusivas de cada janela no Workspace.
        private readonly bool[] workspaceColumns = { true, true, true, true, true };
        private bool workspaceOperationsVisible = true;
        private Action applyWorkspaceAppearance;

        public WorkspaceOptions WorkspaceOptions { get; set; }

        // GDOM126 - integra a janela ao sistema nativo de Workspaces do NinjaTrader.
        // Cada Guardian DOM aberto e salvo no Workspace recebe uma identidade própria;
        // assim, ao reabrir o Workspace, o NinjaTrader recria somente as instâncias
        // que estavam salvas nele (inclusive múltiplas janelas), sem autoabrir GDOMs extras.
        public void Save(XDocument document, XElement element)
        {
            // Cada instância grava suas próprias colunas e seu painel de negociação.
            if (element == null) return;
            element.SetAttributeValue("GDOM129Operations", workspaceOperationsVisible ? "1" : "0");
            string columnFlags = "";
            for (int c = 0; c < workspaceColumns.Length; c++)
                columnFlags += workspaceColumns[c] ? "1" : "0";
            element.SetAttributeValue("GDOM129Columns", columnFlags);
        }

        public void Restore(XDocument document, XElement element)
        {
            // Restaurar somente esta janela; não criar outras instâncias.
            if (element == null) return;
            string columns = (string)element.Attribute("GDOM129Columns");
            if (!string.IsNullOrEmpty(columns) && columns.Length == 5)
                for (int c = 0; c < 5; c++)
                    workspaceColumns[c] = columns[c] != '0';
            string panel = (string)element.Attribute("GDOM129Operations");
            if (panel != null) workspaceOperationsVisible = panel != "0";
            if (applyWorkspaceAppearance != null)
                Dispatcher.BeginInvoke(new Action(() => applyWorkspaceAppearance()),
                    DispatcherPriority.Loaded);
        }

        public GuardianDomWindow()
        {
            Loaded += (o, e) =>
            {
                if (WorkspaceOptions == null)
                    WorkspaceOptions = new WorkspaceOptions(
                        "GuardianDOM-" + Guid.NewGuid().ToString("N"), this);
            };
            // Estrutura da janela baseada diretamente na GuardianWindow original.
            Caption = "Guardian DOM";
            Width = 555;
            Height = 760;
            // GDOM119 - redimensionamento horizontal livre pelo usuario.
            // Sem largura minima fixa do GuardianDOM: a borda da janela pode ser arrastada
            // para a largura desejada, e as colunas visiveis continuam se adaptando.
            MinWidth = 0;
            ResizeMode = ResizeMode.CanResize;
            MinHeight = 560;

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SizeToContent = SizeToContent.Manual;
            Background = new SolidColorBrush(Color.FromRgb(20, 25, 31));

            LoadLadderBackgroundColor();
            startupInstrumentName = ReadLastInstrumentName();
            instrumentLinkGroup = ReadInstrumentLinkGroup();
            lock (guardianLinkSync)
            {
                if (!guardianLinkWindows.Contains(this))
                    guardianLinkWindows.Add(this);
            }

            Content = BuildInterface();

            // GDOM121B - responsividade conservadora.
            // IMPORTANTE: nao altera ColumnDefinitions da ladder. Assim, colunas
            // ocultas (COMPRA/VENDA) continuam em largura zero como na GDOM120.
            SizeChanged += (s, e) => ApplyCompactOuterLayout();
            Loaded += (s, e) => ApplyCompactOuterLayout();

            // V0.9.9.88 - persistencia explicita do ultimo ativo.
            // O nome e salvo em GuardianDOM_LastInstrument.txt e a restauracao manual
            // so comeca depois que a janela terminou de carregar. Isso evita depender
            // do LastUsedGroup do InstrumentSelector.
            Loaded += GuardianDomWindow_LoadedRestoreInstrument;
            Loaded += GuardianDomWindow_LoadedInstallTitleLink;


            depthRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            depthRefreshTimer.Tick += OnDepthRefreshTimerTick;
            depthRefreshTimer.Start();

            // GDOM109 PREÇO EM TEMPO REAL:
            // drena os eventos de mercado em alta frequência e atualiza apenas
            // BID / LAST / ASK. A ladder pesada continua consolidada a 10 Hz.
            fastPriceRefreshTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            fastPriceRefreshTimer.Tick += OnFastPriceRefreshTimerTick;
            fastPriceRefreshTimer.Start();

            Closed += GuardianDomWindow_Closed;
        }

        private void GuardianDomWindow_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                guardianHwndSource = HwndSource.FromHwnd(hwnd);
                if (guardianHwndSource != null)
                    guardianHwndSource.AddHook(GuardianDomWindow_WndProc);

                IntPtr systemMenu = GetSystemMenu(hwnd, false);
                if (systemMenu != IntPtr.Zero)
                {
                    // V0.9.9.94 - adiciona ao menu de sistema REAL da janela.
                    // AppendMenu evita depender das posições internas que o NinjaTrader/WPF
                    // pode reconstruir depois de SourceInitialized.
                    AppendMenu(systemMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
                    AppendMenu(systemMenu, MF_STRING,
                        new UIntPtr((uint)GuardianDuplicateWindowCommand), "Janela Duplicar");
                    DrawMenuBar(hwnd);
                }
            }
            catch { }
        }

        private IntPtr GuardianDomWindow_WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112;
            if (msg == WM_SYSCOMMAND)
            {
                int command = unchecked((int)(wParam.ToInt64() & 0xFFF0));
                if (command == GuardianDuplicateWindowCommand)
                {
                    DuplicateGuardianDomWindow();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void DuplicateGuardianDomWindow()
        {
            string instrumentName = currentInstrument != null ? currentInstrument.FullName : ReadLastInstrumentName();
            int periodMinutes = selectedPeriodMinutes;
            int quantity = quantitySelector != null ? quantitySelector.Value : 1;

            GuardianDomWindow duplicate = new GuardianDomWindow();
            duplicate.Show();

            duplicate.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    duplicate.instrumentLinkGroup = instrumentLinkGroup;
                    duplicate.SaveInstrumentLinkGroup();

                    duplicate.selectedPeriodMinutes = periodMinutes;
                    if (duplicate.periodSelector != null)
                    {
                        string wanted = periodMinutes.ToString() + " min";
                        for (int i = 0; i < duplicate.periodSelector.Items.Count; i++)
                        {
                            if (string.Equals(duplicate.periodSelector.Items[i].ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                            {
                                duplicate.periodSelector.SelectedIndex = i;
                                break;
                            }
                        }
                    }

                    if (duplicate.quantitySelector != null)
                        duplicate.quantitySelector.Value = quantity;

                    if (!string.IsNullOrWhiteSpace(instrumentName))
                    {
                        Instrument instrument = Instrument.GetInstrument(instrumentName);
                        if (instrument != null && duplicate.instrumentSelector != null)
                        {
                            duplicate.startupInstrumentName = instrument.FullName;
                            duplicate.instrumentSelector.Instrument = instrument;
                        }
                    }
                }
                catch { }
            }));
        }

        private UIElement BuildInterface()
        {
            Grid root = new Grid
            {
                // A raiz acompanha a altura da janela; a linha da ladder (Star)
                // recebe todo o espaço vertical adicional ao redimensionar.
                Background = new SolidColorBrush(Color.FromRgb(43, 43, 46)),
                Margin = new Thickness(-7, 0, -7, -7)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(88, 88, 88)),
                Padding = new Thickness(10)
            };

            header.Child = new TextBlock
            {
                Text = "GUARDIAN DOM",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Grid selectors = new Grid
            {
                Margin = new Thickness(8, 8, 8, 6)
            };

            selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.0, GridUnitType.Star) });
            selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
            selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            selectors.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            selectors.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel instrumentPanel = CreateSelectorPanel("ATIVO");
            instrumentSelector = new InstrumentSelector
            {
                Margin = new Thickness(0, 4, 4, 0)
            };

            instrumentSelector.InstrumentChanged += OnInstrumentChanged;
            instrumentPanel.Children.Add(instrumentSelector);
            Grid.SetColumn(instrumentPanel, 0);
            Grid.SetRow(instrumentPanel, 0);
            selectors.Children.Add(instrumentPanel);

            StackPanel accountPanel = CreateSelectorPanel("CONTA");
            accountSelector = new AccountSelector
            {
                Margin = new Thickness(4, 4, 4, 0)
            };
            accountPanel.Children.Add(accountSelector);
            Grid.SetColumn(accountPanel, 1);
            Grid.SetRow(accountPanel, 0);
            selectors.Children.Add(accountPanel);

            // V0.9.9.3 ATM: QTD voltou a ficar visível; a entrada usa a quantidade escolhida no GuardianDOM.
            StackPanel quantityPanel = CreateSelectorPanel("QTD");
            quantitySelector = new QuantityUpDown
            {
                Value = 1,
                Margin = new Thickness(4, 4, 0, 0)
            };
            quantityPanel.Children.Add(quantitySelector);
            Grid.SetColumn(quantityPanel, 2);
            Grid.SetRow(quantityPanel, 0);
            selectors.Children.Add(quantityPanel);

            // V0.9.9.89 - escolha do período diretamente no Guardian DOM.
            StackPanel periodPanel = CreateSelectorPanel("PERÍODO");
            periodSelector = new ComboBox
            {
                Margin = new Thickness(4, 4, 0, 0),
                Height = 23,
                ToolTip = "Janela usada em COMPRA / VENDA / DELTA"
            };

            int[] periodOptions = new int[] { 1, 3, 5, 15, 30, 60 };
            for (int pi = 0; pi < periodOptions.Length; pi++)
                periodSelector.Items.Add(periodOptions[pi] + " min");

            selectedPeriodMinutes = ReadPeriodMinutes();
            int periodIndex = Array.IndexOf(periodOptions, selectedPeriodMinutes);
            if (periodIndex < 0)
            {
                selectedPeriodMinutes = 30;
                periodIndex = Array.IndexOf(periodOptions, 30);
            }

            periodSelector.SelectedIndex = periodIndex;
            periodSelector.SelectionChanged += (s, e) =>
            {
                if (periodSelector.SelectedItem == null)
                    return;

                string rawPeriod = periodSelector.SelectedItem.ToString().Replace(" min", "").Trim();
                int minutes;
                if (!int.TryParse(rawPeriod, out minutes) || minutes <= 0)
                    return;

                selectedPeriodMinutes = minutes;
                SavePeriodMinutes();
                LoadHistoricalFlowForPeriod();
                UpdateDisplay();
            };

            periodPanel.Children.Add(periodSelector);
            Grid.SetColumn(periodPanel, 3);
            Grid.SetRow(periodPanel, 0);
            selectors.Children.Add(periodPanel);

            StackPanel atmPanel = CreateSelectorPanel("ESTRATÉGIA ATM");
            atmStrategySelector = new NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector
            {
                Id = Guid.NewGuid().ToString("N"),
                Margin = new Thickness(0, 4, 0, 0)
            };
            atmStrategySelector.SetBinding(
                NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector.AccountProperty,
                new Binding { Source = accountSelector, Path = new PropertyPath("SelectedAccount") });
            // V0.9.9.17: não escrever QTD em LinkedQuantity.
            // O AtmStrategySelector mantém internamente a configuração do template ATM.
            atmPanel.Children.Add(atmStrategySelector);

            // V0.9.9.17: sincroniza QTD UMA VEZ pela EntryQuantity, sem binding em LinkedQuantity.
            atmDiagTimer = new System.Windows.Threading.DispatcherTimer();
            atmDiagTimer.Interval = TimeSpan.FromMilliseconds(300);
            atmDiagTimer.Tick += AtmDiagTimer_Tick;
            atmDiagTimer.Start();
            Grid.SetRow(atmPanel, 1);
            Grid.SetColumn(atmPanel, 0);
            Grid.SetColumnSpan(atmPanel, 4);
            selectors.Children.Add(atmPanel);

            Grid.SetRow(selectors, 1);
            root.Children.Add(selectors);

            Grid quotePanel = new Grid
            {
                Margin = new Thickness(8, 0, 8, 6),
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 34))
            };

            quotePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quotePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quotePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            bidValue = AddQuoteBox(quotePanel, "BID", "--", 0, Brushes.Green);
            lastValue = AddQuoteBox(quotePanel, "LAST", "--", 1, new SolidColorBrush(Color.FromRgb(62, 62, 65)));
            askValue = AddQuoteBox(quotePanel, "ASK", "--", 2, Brushes.DarkRed);

            Grid.SetRow(quotePanel, 2);
            root.Children.Add(quotePanel);

            Grid columnHeader = new Grid
            {
                Margin = new Thickness(8, 2, 8, 0),
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 34))
            };

            ColumnDefinition headerBuyCol = new ColumnDefinition { Width = new GridLength(90) };
            ColumnDefinition headerPriceCol = new ColumnDefinition { Width = new GridLength(100) };
            ColumnDefinition headerSellCol = new ColumnDefinition { Width = new GridLength(90) };
            ColumnDefinition headerVolumeCol = new ColumnDefinition { Width = new GridLength(75) };
            ColumnDefinition headerDeltaCol = new ColumnDefinition { Width = new GridLength(115) };

            columnHeader.ColumnDefinitions.Add(headerBuyCol);
            columnHeader.ColumnDefinitions.Add(headerPriceCol);
            columnHeader.ColumnDefinitions.Add(headerSellCol);
            columnHeader.ColumnDefinitions.Add(headerVolumeCol);
            columnHeader.ColumnDefinitions.Add(headerDeltaCol);

            // V0.9.9.78 - cabeçalhos arrastáveis para reordenar as colunas.
            Border[] draggableHeaders = new Border[5];
            draggableHeaders[0] = AddHeaderCell(columnHeader, "COMPRA", 0);
            draggableHeaders[1] = AddHeaderCell(columnHeader, "PREÇO", 1);
            draggableHeaders[2] = AddHeaderCell(columnHeader, "VENDA", 2);
            draggableHeaders[3] = AddHeaderCell(columnHeader, "VOLUME", 3);
            draggableHeaders[4] = AddHeaderCell(columnHeader, "DELTA", 4);

            // V0.9.9.38 - linha inferior contínua para destacar o cabeçalho da ladder.
            Border headerBottomSeparator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(105, 105, 105)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };
            Grid.SetColumn(headerBottomSeparator, 0);
            Grid.SetColumnSpan(headerBottomSeparator, 5);
            Panel.SetZIndex(headerBottomSeparator, 850);
            columnHeader.Children.Add(headerBottomSeparator);

            // V0.9.9.42 - linha superior no MESMO Grid do cabeçalho.
            // Não envolve/reinsere columnHeader no layout principal.
            Border headerTopSeparator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(105, 105, 105)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            Grid.SetColumn(headerTopSeparator, 0);
            Grid.SetColumnSpan(headerTopSeparator, 5);
            Panel.SetZIndex(headerTopSeparator, 850);
            columnHeader.Children.Add(headerTopSeparator);

            Grid.SetRow(columnHeader, 3);
            root.Children.Add(columnHeader);

            ladder = new Grid
            {
                Margin = new Thickness(8, 0, 8, 8),
                Background = LadderBaseBrush
            };

            ColumnDefinition ladderBuyCol = new ColumnDefinition { Width = new GridLength(90), MinWidth = 45 };
            ColumnDefinition ladderPriceCol = new ColumnDefinition { Width = new GridLength(100), MinWidth = 60 };
            ColumnDefinition ladderSellCol = new ColumnDefinition { Width = new GridLength(90), MinWidth = 45 };
            ColumnDefinition ladderVolumeCol = new ColumnDefinition { Width = new GridLength(75), MinWidth = 50 };
            ColumnDefinition ladderDeltaCol = new ColumnDefinition { Width = new GridLength(115), MinWidth = 60 };

            ladder.ColumnDefinitions.Add(ladderBuyCol);
            ladder.ColumnDefinitions.Add(ladderPriceCol);
            ladder.ColumnDefinitions.Add(ladderSellCol);
            ladder.ColumnDefinitions.Add(ladderVolumeCol);
            ladder.ColumnDefinitions.Add(ladderDeltaCol);

            // V0.9.9.35 - dimensionadores independentes no cabeçalho.
            // Não usam GridSplitter e não entram na árvore da ladder.
            ColumnDefinition[] headerCols = new ColumnDefinition[]
            {
                headerBuyCol, headerPriceCol, headerSellCol, headerVolumeCol, headerDeltaCol
            };
            ColumnDefinition[] ladderCols = new ColumnDefinition[]
            {
                ladderBuyCol, ladderPriceCol, ladderSellCol, ladderVolumeCol, ladderDeltaCol
            };
            double[] minWidths = new double[] { 20, 20, 20, 30, 30 };

            for (int h = 0; h < 4; h++)
            {
                int handleIndex = h;
                Border handle = new Border
                {
                    Width = 5,
                    Background = new SolidColorBrush(Color.FromArgb(150, 105, 105, 105)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Cursor = Cursors.SizeWE
                };

                bool dragging = false;
                double startX = 0;
                double leftStart = 0;
                double rightStart = 0;
                int dragLeftIndex = -1;
                int dragRightIndex = -1;

                handle.MouseLeftButtonDown += (s, e) =>
                {
                    // GDOM120 - nunca redimensiona/reabre coluna oculta.
                    // O divisor usa a coluna visível à esquerda e procura a próxima
                    // coluna visível à direita, pulando COMPRA/VENDA quando ocultas.
                    dragLeftIndex = handleIndex;
                    while (dragLeftIndex >= 0 && headerCols[dragLeftIndex].ActualWidth <= 1)
                        dragLeftIndex--;

                    dragRightIndex = handleIndex + 1;
                    while (dragRightIndex < headerCols.Length && headerCols[dragRightIndex].ActualWidth <= 1)
                        dragRightIndex++;

                    if (dragLeftIndex < 0 || dragRightIndex >= headerCols.Length)
                    {
                        e.Handled = true;
                        return;
                    }

                    dragging = true;
                    startX = e.GetPosition(columnHeader).X;
                    leftStart = headerCols[dragLeftIndex].ActualWidth;
                    rightStart = headerCols[dragRightIndex].ActualWidth;
                    ((Border)s).CaptureMouse();
                    e.Handled = true;
                };

                handle.MouseMove += (s, e) =>
                {
                    if (!dragging || e.LeftButton != MouseButtonState.Pressed)
                        return;

                    double dx = e.GetPosition(columnHeader).X - startX;
                    double newLeft = leftStart + dx;
                    double newRight = rightStart - dx;

                    if (dragLeftIndex < 0 || dragRightIndex < 0)
                        return;

                    if (newLeft < minWidths[dragLeftIndex])
                    {
                        newLeft = minWidths[dragLeftIndex];
                        newRight = leftStart + rightStart - newLeft;
                    }
                    if (newRight < minWidths[dragRightIndex])
                    {
                        newRight = minWidths[dragRightIndex];
                        newLeft = leftStart + rightStart - newRight;
                    }

                    headerCols[dragLeftIndex].Width = new GridLength(newLeft);
                    headerCols[dragRightIndex].Width = new GridLength(newRight);
                    ladderCols[dragLeftIndex].Width = new GridLength(newLeft);
                    ladderCols[dragRightIndex].Width = new GridLength(newRight);
                    e.Handled = true;
                };

                handle.MouseLeftButtonUp += (s, e) =>
                {
                    dragging = false;
                    ((Border)s).ReleaseMouseCapture();
                    e.Handled = true;
                };

                Grid.SetColumn(handle, h);
                Panel.SetZIndex(handle, 1000);
                columnHeader.Children.Add(handle);
            }

            // V0.9.9.36 - linhas verticais entre VENDA | PREÇO | COMPRA | VOLUME | DELTA.
            for (int sep = 0; sep < 4; sep++)
            {
                Border verticalSeparator = new Border
                {
                    Width = 1,
                    Background = new SolidColorBrush(Color.FromRgb(105, 105, 105)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(verticalSeparator, sep);
                Grid.SetRowSpan(verticalSeparator, LadderRows);
                Panel.SetZIndex(verticalSeparator, 900);
                ladder.Children.Add(verticalSeparator);
            }

            // Dimensionador externo na borda direita do DELTA.
            // Ele altera apenas a largura do DELTA e a largura da janela.
            Border deltaOuterHandle = new Border
            {
                Width = 7,
                Background = new SolidColorBrush(Color.FromArgb(150, 105, 105, 105)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.SizeWE
            };

            bool deltaOuterDragging = false;
            double deltaOuterStartScreenX = 0;
            double deltaOuterStartWidth = 0;

            deltaOuterHandle.MouseLeftButtonDown += (s, e) =>
            {
                deltaOuterDragging = true;
                deltaOuterStartScreenX = e.GetPosition(columnHeader).X;
                deltaOuterStartWidth = headerDeltaCol.ActualWidth;
                ((Border)s).CaptureMouse();
                e.Handled = true;
            };

            deltaOuterHandle.MouseMove += (s, e) =>
            {
                if (!deltaOuterDragging || e.LeftButton != MouseButtonState.Pressed)
                    return;

                double dx = e.GetPosition(columnHeader).X - deltaOuterStartScreenX;
                double newDeltaWidth = Math.Max(60, deltaOuterStartWidth + dx);
                headerDeltaCol.Width = new GridLength(newDeltaWidth);
                ladderDeltaCol.Width = new GridLength(newDeltaWidth);

                e.Handled = true;
            };

            deltaOuterHandle.MouseLeftButtonUp += (s, e) =>
            {
                deltaOuterDragging = false;
                ((Border)s).ReleaseMouseCapture();
                e.Handled = true;
            };

            Grid.SetColumn(deltaOuterHandle, 4);
            Panel.SetZIndex(deltaOuterHandle, 1100);
            columnHeader.Children.Add(deltaOuterHandle);

            bidCells = new TextBlock[LadderRows];
            priceCells = new TextBlock[LadderRows];
            askCells = new TextBlock[LadderRows];
            bidBorders = new Border[LadderRows];
            priceBorders = new Border[LadderRows];
            askBorders = new Border[LadderRows];
            buyFlowGrids = new Grid[LadderRows];
            sellFlowGrids = new Grid[LadderRows];
            buyFlowBars = new Border[LadderRows];
            sellFlowBars = new Border[LadderRows];
            buyImbalanceBorders = new Border[LadderRows];
            sellImbalanceBorders = new Border[LadderRows];
            volumeCells = new TextBlock[LadderRows];
            volumeBorders = new Border[LadderRows];
            volumeGrids = new Grid[LadderRows];
            volumeBars = new Border[LadderRows];
            deltaCells = new TextBlock[LadderRows];
            deltaNegativeCells = new TextBlock[LadderRows];
            deltaPositiveCells = new TextBlock[LadderRows];
            deltaBorders = new Border[LadderRows];
            deltaGrids = new Grid[LadderRows];
            deltaPositiveBars = new Border[LadderRows];
            deltaNegativeBars = new Border[LadderRows];

            for (int i = 0; i < LadderRows; i++)
            {
                ladder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LadderRowHeight) });

                bidCells[i] = AddLadderCell(ladder, "", i, 0,
                    LadderBaseBrush, out bidBorders[i]);

                priceCells[i] = AddLadderCell(ladder, "--", i, 1,
                    LadderBaseBrush, out priceBorders[i]);

                // GDOM113 - cada nível de PREÇO permanece uma célula visual independente.
                // A tendência colore o fundo, mas esta borda escura fixa preserva
                // a separação horizontal como no SuperDOM antigo.
                priceBorders[i].BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 48));
                priceBorders[i].BorderThickness = new Thickness(0, 0, 1, 1);

                int previewRow = i;
                priceCells[i].Cursor = Cursors.Hand;
                priceCells[i].MouseLeftButtonDown +=
                    (s, e) => PreviewOrderAtRow(previewRow, "COMPRA");
                priceCells[i].MouseRightButtonDown +=
                    (s, e) => PreviewOrderAtRow(previewRow, "VENDA");

                // V0.9.9.61 - rolagem direta na coluna PREÇO.
                // Cada passo da roda desloca 1 tick para permitir buscar um preço específico.
                // A rolagem coloca a ladder em modo MANUAL; CENTRALIZAR devolve ao modo AUTO.
                priceCells[i].MouseWheel += (s, e) =>
                {
                    int wheelSteps = Math.Max(1, Math.Abs(e.Delta) / 120);
                    ladderOffsetTicks += e.Delta > 0 ? wheelSteps : -wheelSteps;
                    ladderManualNavigation = true;
                    UpdateDisplay();
                    e.Handled = true;
                };

                askCells[i] = AddLadderCell(ladder, "", i, 2,
                    LadderBaseBrush, out askBorders[i]);

                // V0.9.9.69 - COMPRA: ancorada na DIREITA da coluna; cresce da DIREITA para a ESQUERDA.
                buyFlowGrids[i] = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, ClipToBounds = true };
                bidBorders[i].Child = null;
                buyFlowBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0, 128, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Width = 0
                };
                buyImbalanceBorders[i] = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(255, 185, 35)),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(1),
                    IsHitTestVisible = false
                };
                buyFlowGrids[i].Children.Add(buyFlowBars[i]);
                buyFlowGrids[i].Children.Add(bidCells[i]);
                buyFlowGrids[i].Children.Add(buyImbalanceBorders[i]);
                bidBorders[i].Child = buyFlowGrids[i];
                bidBorders[i].Background = LadderBaseBrush;

                // V0.9.9.69 - VENDA: ancorada na ESQUERDA da coluna; cresce da ESQUERDA para a DIREITA.
                sellFlowGrids[i] = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, ClipToBounds = true };
                askBorders[i].Child = null;
                sellFlowBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(139, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0
                };
                sellImbalanceBorders[i] = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(255, 185, 35)),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(1),
                    IsHitTestVisible = false
                };
                sellFlowGrids[i].Children.Add(sellFlowBars[i]);
                sellFlowGrids[i].Children.Add(askCells[i]);
                sellFlowGrids[i].Children.Add(sellImbalanceBorders[i]);
                askBorders[i].Child = sellFlowGrids[i];
                askBorders[i].Background = LadderBaseBrush;

                volumeCells[i] = AddLadderCell(ladder, "", i, 3,
                    LadderBaseBrush, out volumeBorders[i]);

                volumeGrids[i] = new Grid();
                volumeBorders[i].Child = null;
                volumeBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(105, 105, 110)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0
                };
                volumeGrids[i].Children.Add(volumeBars[i]);
                volumeGrids[i].Children.Add(volumeCells[i]);
                volumeBorders[i].Child = volumeGrids[i];

                deltaBorders[i] = new Border
                {
                    // V0.9.9.49 - mesmo fundo-base das colunas VENDA/COMPRA.
                    Background = LadderBaseBrush,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(72, 72, 75)),
                    BorderThickness = new Thickness(0, 0, 1, 1)
                };
                Grid.SetRow(deltaBorders[i], i);
                Grid.SetColumn(deltaBorders[i], 4);
                ladder.Children.Add(deltaBorders[i]);

                deltaGrids[i] = new Grid { ClipToBounds = true };
                deltaGrids[i].ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                deltaGrids[i].ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                deltaNegativeBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(139, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Width = 0
                };
                Grid.SetColumn(deltaNegativeBars[i], 1);

                deltaPositiveBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0, 128, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Width = 0
                };
                Grid.SetColumn(deltaPositiveBars[i], 0);

                // Mantém deltaCells para compatibilidade com a rotina de limpeza,
                // mas a exibição passa a usar textos espelhados em torno do zero.
                deltaCells[i] = new TextBlock { Text = "", Visibility = Visibility.Collapsed };

                deltaNegativeCells[i] = new TextBlock
                {
                    Text = "",
                    Foreground = Brushes.WhiteSmoke,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Left,
                    Margin = new Thickness(4, 0, 2, 0)
                };
                Grid.SetColumn(deltaNegativeCells[i], 1);

                deltaPositiveCells[i] = new TextBlock
                {
                    Text = "",
                    Foreground = Brushes.WhiteSmoke,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(2, 0, 4, 0)
                };
                Grid.SetColumn(deltaPositiveCells[i], 0);

                deltaGrids[i].Children.Add(deltaNegativeBars[i]);
                deltaGrids[i].Children.Add(deltaPositiveBars[i]);
                deltaGrids[i].Children.Add(deltaNegativeCells[i]);
                deltaGrids[i].Children.Add(deltaPositiveCells[i]);
                deltaBorders[i].Child = deltaGrids[i];
            }

            // GDOM107 - altura fixa e compacta. A ladder possui 41 níveis reais.
            // Com o painel operacional ativo, ocultamos somente as linhas externas que
            // não cabem na área disponível; sem o painel, as 41 linhas ficam visíveis.
            // Assim a altura de cada tick nunca é esticada pelo Grid.
            for (int rr = 0; rr < PanelHiddenOuterRows; rr++)
            {
                ladder.RowDefinitions[rr].Height = new GridLength(0);
                ladder.RowDefinitions[LadderRows - 1 - rr].Height = new GridLength(0);
            }

            // V0.9.9.78 - DRAG COLUMN REORDER.
            // Arraste qualquer cabeçalho sobre outro. A coluna inteira acompanha o título.
            Border[][] logicalColumnBorders = new Border[][]
            {
                bidBorders, priceBorders, askBorders, volumeBorders, deltaBorders
            };

            // V0.9.9.79 - reaplica a ultima ordem escolhida ao abrir o GuardianDOM.
            LoadColumnOrder(draggableHeaders, logicalColumnBorders);

            int draggedLogicalColumn = -1;
            for (int dc = 0; dc < draggableHeaders.Length; dc++)
            {
                int logicalIndex = dc;
                Border hdr = draggableHeaders[dc];
                hdr.Tag = logicalIndex;
                hdr.Cursor = Cursors.Hand;
                hdr.ToolTip = "Arraste para mudar a coluna de lugar";
                hdr.AllowDrop = true;

                hdr.MouseLeftButtonDown += (s, e) =>
                {
                    draggedLogicalColumn = logicalIndex;
                    e.Handled = true;
                };

                hdr.MouseMove += (s, e) =>
                {
                    if (draggedLogicalColumn != logicalIndex || e.LeftButton != MouseButtonState.Pressed)
                        return;
                    DragDrop.DoDragDrop((DependencyObject)s, logicalIndex, DragDropEffects.Move);
                    e.Handled = true;
                };

                hdr.Drop += (s, e) =>
                {
                    if (!e.Data.GetDataPresent(typeof(int))) return;
                    int sourceLogical = (int)e.Data.GetData(typeof(int));
                    int targetLogical = logicalIndex;
                    if (sourceLogical == targetLogical) return;

                    int sourceVisual = Grid.GetColumn(draggableHeaders[sourceLogical]);
                    int targetVisual = Grid.GetColumn(draggableHeaders[targetLogical]);

                    // Troca os cabeçalhos.
                    Grid.SetColumn(draggableHeaders[sourceLogical], targetVisual);
                    Grid.SetColumn(draggableHeaders[targetLogical], sourceVisual);

                    // Troca todas as células das duas colunas, preservando conteúdo,
                    // histogramas, cliques de preço e demais funções internas.
                    for (int rr = 0; rr < LadderRows; rr++)
                    {
                        Grid.SetColumn(logicalColumnBorders[sourceLogical][rr], targetVisual);
                        Grid.SetColumn(logicalColumnBorders[targetLogical][rr], sourceVisual);
                    }

                    // V0.9.9.79 - grava imediatamente a nova ordem.
                    SaveColumnOrder(draggableHeaders);

                    draggedLogicalColumn = -1;
                    e.Handled = true;
                };

                hdr.MouseLeftButtonUp += (s, e) => draggedLogicalColumn = -1;
            }

            Grid.SetRow(ladder, 4);
            root.Children.Add(ladder);

            Border statusBorder = new Border
            {
                Margin = new Thickness(8, 0, 8, 8),
                Padding = new Thickness(8),
                // V0.9.9.50 - bloco inferior com o mesmo fundo-base cinza usado na região superior/ladder.
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 58)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 84)),
                BorderThickness = new Thickness(1)
            };

            connectionStatus = new TextBlock
            {
                Text = "V0.9.9.94 JANELA DUPLICAR • ENVIO NA CONTA SELECIONADA",
                Foreground = Brushes.Gold,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            StackPanel statusPanel = new StackPanel();

            statusPanel.Children.Add(connectionStatus);

            aggressionBalanceStatus = new TextBlock
            {
                Text = "SALDO AGRESSÃO: 0",
                Foreground = Brushes.WhiteSmoke,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(4, 4, 4, 0)
            };
            statusPanel.Children.Add(aggressionBalanceStatus);

            profileStatus = new TextBlock
            {
                Text = "POC: --   •   VAH: --   •   VAL: --",
                Foreground = Brushes.LightGray,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            orderStateStatus = new TextBlock
            {
                Text = "ORDEM: SEM ORDEM",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(4, 4, 4, 0)
            };
            statusPanel.Children.Add(orderStateStatus);

            StackPanel navigationPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            };

            Button ladderUpButton = new Button
            {
                Content = "▲ +30",
                Width = 72,
                Height = 25,
                Margin = new Thickness(2)
            };
            ladderUpButton.Click += (s, e) =>
            {
                ladderOffsetTicks += 30;
                ladderManualNavigation = true;
                UpdateDisplay();
            };

            Button ladderCenterButton = new Button
            {
                Content = "CENTRALIZAR",
                Width = 110,
                Height = 25,
                Margin = new Thickness(2)
            };
            ladderCenterButton.Click += (s, e) =>
            {
                ladderOffsetTicks = 0;
                ladderManualNavigation = false;
                ladderDisplayCenter = double.NaN; // força centralização imediata
                UpdateDisplay();
            };

            Button ladderDownButton = new Button
            {
                Content = "▼ -30",
                Width = 72,
                Height = 25,
                Margin = new Thickness(2)
            };
            ladderDownButton.Click += (s, e) =>
            {
                ladderOffsetTicks -= 30;
                ladderManualNavigation = true;
                UpdateDisplay();
            };

            navigationPanel.Children.Add(ladderUpButton);
            navigationPanel.Children.Add(ladderCenterButton);
            navigationPanel.Children.Add(ladderDownButton);
            statusPanel.Children.Add(navigationPanel);

            // V0.9.9.67 - padrão visual do NinjaTrader:
            // COMPRA à esquerda, PnL ao centro e VENDA à direita.
            Grid quickTradePanel = new Grid
            {
                Margin = new Thickness(8, 5, 8, 0)
            };
            // V0.9.9.70 - mantém histogramas da 69; botões: COMPRA 33% | PnL 34% | VENDA 33%.
            quickTradePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33, GridUnitType.Star) });
            quickTradePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34, GridUnitType.Star) });
            quickTradePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33, GridUnitType.Star) });

            marketBuyButton = new Button
            {
                Content = "COMPRA MERC.",
                Height = 30,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Green,
                Foreground = Brushes.White
            };
            marketBuyButton.Click += (s, e) => SubmitMarketEntry(true);
            Grid.SetColumn(marketBuyButton, 0);
            quickTradePanel.Children.Add(marketBuyButton);

            Border pnlBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 34)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 82)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Height = 30
            };
            marketPnlValue = new TextBlock
            {
                Text = "PnL  $ 0,00",
                Foreground = Brushes.WhiteSmoke,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            pnlBox.Child = marketPnlValue;
            pnlBox.Cursor = Cursors.Hand;
            pnlBox.ToolTip = "Clique para alternar: moeda → ticks → pontos";
            pnlBox.MouseLeftButtonDown += (s, e) =>
            {
                marketPnlDisplayMode = (marketPnlDisplayMode + 1) % 3;
                UpdateMarketPnl();
                e.Handled = true;
            };
            Grid.SetColumn(pnlBox, 1);
            quickTradePanel.Children.Add(pnlBox);

            marketSellButton = new Button
            {
                Content = "VENDA MERC.",
                Height = 30,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 0, 0),
                Background = Brushes.DarkRed,
                Foreground = Brushes.White
            };
            marketSellButton.Click += (s, e) => SubmitMarketEntry(false);
            Grid.SetColumn(marketSellButton, 2);
            quickTradePanel.Children.Add(marketSellButton);

            statusPanel.Children.Add(quickTradePanel);

            flattenButton = new Button
            {
                Content = "FECHAMENTO",
                Height = 30,
                Margin = new Thickness(8, 4, 8, 0),
                Background = Brushes.DarkGoldenrod,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            flattenButton.Click += FlattenButton_Click;
            statusPanel.Children.Add(flattenButton);

            sendPreviewButton = new Button
            {
                Content = "ENVIAR ORDEM — CONTA SELECIONADA",
                Height = 28,
                Margin = new Thickness(8, 5, 8, 0),
                IsEnabled = false
            };
            sendPreviewButton.Click += SendPreviewButton_Click;
            // V0.9.9.70 - botão legado ENVIAR ORDEM não é mais exibido.

            cancelOrderButton = new Button
            {
                Content = "CANCELAR — SEM ORDEM",
                Height = 28,
                Margin = new Thickness(8, 4, 8, 0),
                IsEnabled = false
            };
            cancelOrderButton.Click += CancelOrderButton_Click;
            // V0.9.9.70 - botão legado CANCELAR não é mais exibido.

            statusPanel.Children.Add(profileStatus);
            statusBorder.Child = statusPanel;

            Grid.SetRow(statusBorder, 5);
            root.Children.Add(statusBorder);

            // V0.9.9.60 - menu de personalização visual, inspirado no menu do SuperDOM.
            // IMPORTANTE: o menu não é anexado à ladder porque o botão direito sobre PREÇO
            // continua reservado para a prévia de VENDA. Ele abre nas áreas superior/inferior
            // do GuardianDOM (cabeçalho, seletores, cotações, cabeçalho das colunas e status).
            ContextMenu customizationMenu = new ContextMenu();

            // V0.9.9.94 - duplicação funcional da janela.
            // O NTWindow customizado não expõe automaticamente o comando nativo de
            // duplicação usado pelo Chart/SuperDOM, então disponibilizamos a mesma
            // ação no menu de contexto do próprio Guardian DOM.
            MenuItem duplicateWindowMenu = new MenuItem { Header = "Janela Duplicar" };
            duplicateWindowMenu.Click += (s, e) => DuplicateGuardianDomWindow();
            customizationMenu.Items.Add(duplicateWindowMenu);
            customizationMenu.Items.Add(new Separator());

            customizationMenu.Items.Add(CreateInstrumentLinkMenu());
            customizationMenu.Items.Add(new Separator());

            MenuItem autoCenterMenu = new MenuItem { Header = "Centro automático", IsCheckable = true, IsChecked = !ladderManualNavigation };
            autoCenterMenu.Click += (s, e) =>
            {
                if (autoCenterMenu.IsChecked)
                {
                    ladderOffsetTicks = 0;
                    ladderManualNavigation = false;
                    ladderDisplayCenter = double.NaN;
                    UpdateDisplay();
                }
                else
                {
                    ladderManualNavigation = true;
                }
            };
            customizationMenu.Items.Add(autoCenterMenu);

            MenuItem columnsMenu = new MenuItem { Header = "Colunas..." };
            customizationMenu.Items.Add(columnsMenu);

            ColumnDefinition[] customizationHeaderCols = new ColumnDefinition[]
            {
                headerBuyCol, headerPriceCol, headerSellCol, headerVolumeCol, headerDeltaCol
            };
            ColumnDefinition[] customizationLadderCols = new ColumnDefinition[]
            {
                ladderBuyCol, ladderPriceCol, ladderSellCol, ladderVolumeCol, ladderDeltaCol
            };
            string[] customizationColumnNames = new string[] { "COMPRA", "PREÇO", "VENDA", "VOLUME", "DELTA" };
            double[] customizationDefaultWidths = new double[] { 90, 100, 90, 75, 115 };
            double[] customizationMinWidths = new double[] { 20, 20, 20, 30, 30 };
            double[] customizationSavedWidths = new double[] { 90, 100, 90, 75, 115 };

            for (int c = 0; c < customizationColumnNames.Length; c++)
            {
                int columnIndex = c;
                MenuItem columnItem = new MenuItem
                {
                    Header = customizationColumnNames[c],
                    IsCheckable = true,
                    IsChecked = workspaceColumns[c]
                };
                columnItem.Click += (s, e) =>
                {
                    workspaceColumns[columnIndex] = columnItem.IsChecked;
                    // V0.9.9.64 - RESPONSIVE COLUMNS FIX:
                    // ocultar uma coluna nao pode reduzir a largura total da ladder.
                    // As colunas visiveis passam a usar STAR proporcional ao tamanho
                    // padrao, ocupando automaticamente 100% da area disponivel.
                    if (!columnItem.IsChecked)
                    {
                        int visualIndex = Grid.GetColumn(draggableHeaders[columnIndex]);
                        double currentWidth = customizationHeaderCols[visualIndex].ActualWidth;
                        if (currentWidth > 1)
                            customizationSavedWidths[columnIndex] = currentWidth;
                    }

                    for (int rc = 0; rc < customizationColumnNames.Length; rc++)
                    {
                        MenuItem rcItem = columnsMenu.Items[rc] as MenuItem;
                        bool visible = rcItem != null && rcItem.IsChecked;

                        int visualRc = Grid.GetColumn(draggableHeaders[rc]);
                        if (!visible)
                        {
                            customizationHeaderCols[visualRc].MinWidth = 0;
                            customizationLadderCols[visualRc].MinWidth = 0;
                            customizationHeaderCols[visualRc].Width = new GridLength(0);
                            customizationLadderCols[visualRc].Width = new GridLength(0);
                        }
                        else
                        {
                            customizationHeaderCols[visualRc].MinWidth = 0;
                            customizationLadderCols[visualRc].MinWidth = customizationMinWidths[rc];
                            double weight = customizationDefaultWidths[rc];
                            customizationHeaderCols[visualRc].Width = new GridLength(weight, GridUnitType.Star);
                            customizationLadderCols[visualRc].Width = new GridLength(weight, GridUnitType.Star);
                        }
                    }
                };
                columnsMenu.Items.Add(columnItem);
            }

            // GDOM123 - presets rápidos de visualização sem alterar a ordem das colunas.
            MenuItem presetsMenu = new MenuItem { Header = "Visualização rápida..." };
            MenuItem presetCompact = new MenuItem { Header = "COMPACTO — Preço + Volume + Delta" };
            MenuItem presetComplete = new MenuItem { Header = "COMPLETO — Todas as colunas" };
            Action<bool> applyPreset = (compact) =>
            {
                for (int rc = 0; rc < customizationColumnNames.Length; rc++)
                {
                    MenuItem rcItem = columnsMenu.Items[rc] as MenuItem;
                    bool visible = compact ? (rc == 1 || rc == 3 || rc == 4) : true;
                    if (rcItem != null) rcItem.IsChecked = visible;
                    workspaceColumns[rc] = visible;
                    int visualRc = Grid.GetColumn(draggableHeaders[rc]);
                    customizationHeaderCols[visualRc].MinWidth = 0;
                    customizationLadderCols[visualRc].MinWidth = visible ? customizationMinWidths[rc] : 0;
                    customizationHeaderCols[visualRc].Width = visible ? new GridLength(customizationDefaultWidths[rc], GridUnitType.Star) : new GridLength(0);
                    customizationLadderCols[visualRc].Width = visible ? new GridLength(customizationDefaultWidths[rc], GridUnitType.Star) : new GridLength(0);
                }
                UpdateDisplay();
            };
            presetCompact.Click += (s, e) => applyPreset(true);
            presetComplete.Click += (s, e) => applyPreset(false);
            presetsMenu.Items.Add(presetCompact);
            presetsMenu.Items.Add(presetComplete);
            customizationMenu.Items.Add(presetsMenu);

            // GDOM125 - navegação direta para os níveis do perfil.
            // Mantém a ladder em modo MANUAL e posiciona o nível escolhido no centro.
            MenuItem goToMenu = new MenuItem { Header = "Ir para..." };
            MenuItem goToPoc = new MenuItem { Header = "POC" };
            MenuItem goToVah = new MenuItem { Header = "VAH" };
            MenuItem goToVal = new MenuItem { Header = "VAL" };
            MenuItem goToMarket = new MenuItem { Header = "Preço atual (CENTRALIZAR)" };

            goToPoc.Click += (s, e) => NavigateToProfileLevel(profilePoc);
            goToVah.Click += (s, e) => NavigateToProfileLevel(profileVah);
            goToVal.Click += (s, e) => NavigateToProfileLevel(profileVal);
            goToMarket.Click += (s, e) =>
            {
                ladderOffsetTicks = 0;
                ladderManualNavigation = false;
                ladderDisplayCenter = double.NaN;
                UpdateDisplay();
            };

            goToMenu.Items.Add(goToPoc);
            goToMenu.Items.Add(goToVah);
            goToMenu.Items.Add(goToVal);
            goToMenu.Items.Add(new Separator());
            goToMenu.Items.Add(goToMarket);
            customizationMenu.Items.Add(goToMenu);

            MenuItem indicatorsMenu = new MenuItem { Header = "Indicadores..." };
            customizationMenu.Items.Add(indicatorsMenu);

            MenuItem quotesMenu = new MenuItem { Header = "BID / LAST / ASK", IsCheckable = true, IsChecked = true };
            quotesMenu.Click += (s, e) => quotePanel.Visibility = quotesMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            indicatorsMenu.Items.Add(quotesMenu);

            MenuItem aggressionMenu = new MenuItem { Header = "Saldo de agressão", IsCheckable = true, IsChecked = true };
            aggressionMenu.Click += (s, e) => aggressionBalanceStatus.Visibility = aggressionMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            indicatorsMenu.Items.Add(aggressionMenu);

            // V0.9.9.80 - liga/desliga o destaque visual de imbalance sem alterar
            // os dados, histogramas ou a lógica de envio de ordens.
            MenuItem imbalanceMenu = new MenuItem { Header = "Imbalance diagonal 3x (mín. 20)", IsCheckable = true, IsChecked = imbalanceEnabled };
            imbalanceMenu.Click += (s, e) =>
            {
                imbalanceEnabled = imbalanceMenu.IsChecked;
                UpdateDisplay();
            };
            indicatorsMenu.Items.Add(imbalanceMenu);

            MenuItem orderStateMenu = new MenuItem { Header = "Estado da ordem", IsCheckable = true, IsChecked = true };
            orderStateMenu.Click += (s, e) => orderStateStatus.Visibility = orderStateMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            indicatorsMenu.Items.Add(orderStateMenu);

            MenuItem profileMenu = new MenuItem { Header = "POC / VAH / VAL", IsCheckable = true, IsChecked = true };
            profileMenu.Click += (s, e) => profileStatus.Visibility = profileMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            indicatorsMenu.Items.Add(profileMenu);

            MenuItem navigationMenu = new MenuItem { Header = "Controles de navegação", IsCheckable = true, IsChecked = true };
            navigationMenu.Click += (s, e) => navigationPanel.Visibility = navigationMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            indicatorsMenu.Items.Add(navigationMenu);

            // GDOM105 - modo de acompanhamento por janela.
            // Desmarcar recolhe COMPRA / PnL / VENDA e FECHAMENTO, liberando o espaço
            // vertical para a ladder. POC / VAH / VAL permanece visível e o estado
            // não é compartilhado entre GuardianDOMs vinculados.
            MenuItem orderButtonsMenu = new MenuItem { Header = "Painel de operações", IsCheckable = true, IsChecked = workspaceOperationsVisible };
            orderButtonsMenu.Click += (s, e) =>
            {
                bool panelAtivo = orderButtonsMenu.IsChecked;
                workspaceOperationsVisible = panelAtivo;
                Visibility v = panelAtivo ? Visibility.Visible : Visibility.Collapsed;
                quickTradePanel.Visibility = v;
                if (flattenButton != null) flattenButton.Visibility = v;

                // GDOM105 - no modo de acompanhamento, os controles manuais de navegação
                // também são recolhidos para aproximar o layout do SuperDOM antigo e
                // entregar mais altura útil à ladder. A roda do mouse e o auto-centro
                // continuam disponíveis.
                navigationPanel.Visibility = v;
                navigationMenu.IsChecked = panelAtivo;

                // GDOM107 - altura fixa: com painel mostramos 35 níveis; sem painel, 41.
                // Nenhuma linha é engrossada para preencher o espaço restante.
                if (ladder != null && ladder.RowDefinitions.Count == LadderRows)
                {
                    for (int rr = 0; rr < LadderRows; rr++)
                    {
                        bool outer = rr < PanelHiddenOuterRows || rr >= LadderRows - PanelHiddenOuterRows;
                        ladder.RowDefinitions[rr].Height = (panelAtivo && outer)
                            ? new GridLength(0)
                            : new GridLength(LadderRowHeight);
                    }
                }
                UpdateDisplay();
            };
            // Aplicação idempotente das preferências restauradas pelo Workspace.
            applyWorkspaceAppearance = () =>
            {
                for (int rc = 0; rc < 5; rc++)
                {
                    MenuItem item = columnsMenu.Items[rc] as MenuItem;
                    if (item != null) item.IsChecked = workspaceColumns[rc];
                    int visual = Grid.GetColumn(draggableHeaders[rc]);
                    bool visible = workspaceColumns[rc];
                    customizationHeaderCols[visual].MinWidth = 0;
                    customizationLadderCols[visual].MinWidth = visible ? customizationMinWidths[rc] : 0;
                    customizationHeaderCols[visual].Width = visible
                        ? new GridLength(customizationDefaultWidths[rc], GridUnitType.Star)
                        : new GridLength(0);
                    customizationLadderCols[visual].Width = visible
                        ? new GridLength(customizationDefaultWidths[rc], GridUnitType.Star)
                        : new GridLength(0);
                }
                orderButtonsMenu.IsChecked = workspaceOperationsVisible;
                // Reaproveita o mesmo manipulador de UI (inclusive altura da ladder).
                orderButtonsMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                UpdateDisplay();
            };
            Dispatcher.BeginInvoke(new Action(() => applyWorkspaceAppearance()), DispatcherPriority.Loaded);
            customizationMenu.Items.Add(orderButtonsMenu);

            MenuItem ladderColorMenu = new MenuItem { Header = "Cor de fundo da ladder..." };
            ladderColorMenu.Click += (s, e) => ShowLadderBackgroundColorDialog();
            customizationMenu.Items.Add(ladderColorMenu);

            customizationMenu.Items.Add(new Separator());

            MenuItem alwaysOnTopMenu = new MenuItem { Header = "Sempre no topo", IsCheckable = true, IsChecked = Topmost };
            alwaysOnTopMenu.Click += (s, e) => Topmost = alwaysOnTopMenu.IsChecked;
            customizationMenu.Items.Add(alwaysOnTopMenu);

            MenuItem restoreMenu = new MenuItem { Header = "Restaurar visual padrão" };
            restoreMenu.Click += (s, e) =>
            {
                // V0.9.9.78 - restaura também a ordem original das colunas.
                for (int rc = 0; rc < draggableHeaders.Length; rc++)
                {
                    Grid.SetColumn(draggableHeaders[rc], rc);
                    for (int rr = 0; rr < LadderRows; rr++)
                        Grid.SetColumn(logicalColumnBorders[rc][rr], rc);
                }
                SaveColumnOrder(draggableHeaders);

                for (int c = 0; c < customizationHeaderCols.Length; c++)
                {
                    customizationSavedWidths[c] = customizationDefaultWidths[c];
                    customizationHeaderCols[c].MinWidth = 0;
                    customizationLadderCols[c].MinWidth = customizationMinWidths[c];
                    customizationHeaderCols[c].Width = new GridLength(customizationDefaultWidths[c]);
                    customizationLadderCols[c].Width = new GridLength(customizationDefaultWidths[c]);
                    if (c < columnsMenu.Items.Count && columnsMenu.Items[c] is MenuItem)
                        ((MenuItem)columnsMenu.Items[c]).IsChecked = true;
                    if (c < workspaceColumns.Length) workspaceColumns[c] = true;
                }

                quotePanel.Visibility = Visibility.Visible;
                aggressionBalanceStatus.Visibility = Visibility.Visible;
                orderStateStatus.Visibility = Visibility.Visible;
                profileStatus.Visibility = Visibility.Visible;
                navigationPanel.Visibility = Visibility.Visible;
                quickTradePanel.Visibility = Visibility.Visible;
                if (flattenButton != null) flattenButton.Visibility = Visibility.Visible;
                orderButtonsMenu.IsChecked = true;
                workspaceOperationsVisible = true;
                quotesMenu.IsChecked = true;
                aggressionMenu.IsChecked = true;
                orderStateMenu.IsChecked = true;
                profileMenu.IsChecked = true;
                navigationMenu.IsChecked = true;
                Topmost = false;
                alwaysOnTopMenu.IsChecked = false;
                SetLadderBackgroundColor(Color.FromRgb(55, 55, 58), true);
                ladderOffsetTicks = 0;
                ladderManualNavigation = false;
                ladderDisplayCenter = double.NaN;
                autoCenterMenu.IsChecked = true;
                UpdateDisplay();
            };
            customizationMenu.Items.Add(restoreMenu);

            // Compartilha o mesmo menu nas áreas onde o clique direito não envia ordem.
            header.ContextMenu = customizationMenu;
            selectors.ContextMenu = customizationMenu;
            quotePanel.ContextMenu = customizationMenu;
            columnHeader.ContextMenu = customizationMenu;
            statusBorder.ContextMenu = customizationMenu;

            return root;
        }

        // V0.9.9.89 - persistência do período gráfico.
        private string PeriodSettingsPath()
        {
            try { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GuardianDOM_PeriodMinutes.txt"); }
            catch { return string.Empty; }
        }

        private int ReadPeriodMinutes()
        {
            try
            {
                string path = PeriodSettingsPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return 30;

                int value;
                if (int.TryParse(File.ReadAllText(path).Trim(), out value) &&
                    (value == 1 || value == 3 || value == 5 || value == 15 || value == 30 || value == 60))
                    return value;
            }
            catch { }

            return 30;
        }

        private void SavePeriodMinutes()
        {
            try
            {
                string path = PeriodSettingsPath();
                if (!string.IsNullOrEmpty(path))
                    File.WriteAllText(path, selectedPeriodMinutes.ToString());
            }
            catch { }
        }

        // V0.9.9.79 - persistencia da ordem visual das colunas.
        private string ColumnOrderSettingsPath()
        {
            try { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GuardianDOM_ColumnOrder.txt"); }
            catch { return string.Empty; }
        }

        private void SaveColumnOrder(Border[] headers)
        {
            try
            {
                string path = ColumnOrderSettingsPath();
                if (string.IsNullOrEmpty(path) || headers == null || headers.Length != 5) return;
                int[] visualToLogical = new int[5];
                for (int logical = 0; logical < headers.Length; logical++)
                {
                    int visual = Grid.GetColumn(headers[logical]);
                    if (visual >= 0 && visual < visualToLogical.Length) visualToLogical[visual] = logical;
                }
                File.WriteAllText(path, string.Join(",", visualToLogical));
            }
            catch { }
        }

        private void LoadColumnOrder(Border[] headers, Border[][] logicalBorders)
        {
            try
            {
                string path = ColumnOrderSettingsPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || headers == null || logicalBorders == null) return;
                string[] parts = File.ReadAllText(path).Trim().Split(',');
                if (parts.Length != 5) return;
                bool[] used = new bool[5];
                for (int visual = 0; visual < 5; visual++)
                {
                    int logical;
                    if (!int.TryParse(parts[visual], out logical) || logical < 0 || logical >= 5 || used[logical]) return;
                    used[logical] = true;
                    Grid.SetColumn(headers[logical], visual);
                    for (int rr = 0; rr < LadderRows; rr++)
                        Grid.SetColumn(logicalBorders[logical][rr], visual);
                }
            }
            catch { }
        }

        // V0.9.9.74 - aplica a mesma cor-base a todas as colunas sem remover divisórias,
        // histogramas nem os realces temporários de BID/ASK/LAST.
        private void SetLadderBackgroundColor(Color color, bool save)
        {
            ladderBaseColor = color;
            Brush brush = LadderBaseBrush;
            if (ladder != null) ladder.Background = brush;

            for (int i = 0; i < LadderRows; i++)
            {
                if (bidBorders != null && bidBorders[i] != null) bidBorders[i].Background = brush;
                if (priceBorders != null && priceBorders[i] != null) priceBorders[i].Background = brush;
                if (askBorders != null && askBorders[i] != null) askBorders[i].Background = brush;
                if (volumeBorders != null && volumeBorders[i] != null) volumeBorders[i].Background = brush;
                if (deltaBorders != null && deltaBorders[i] != null) deltaBorders[i].Background = brush;
            }

            if (save) SaveLadderBackgroundColor();
            UpdateDisplay();
        }

        private string LadderColorSettingsPath()
        {
            try { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GuardianDOM_LadderColor.txt"); }
            catch { return string.Empty; }
        }

        private void SaveLadderBackgroundColor()
        {
            try
            {
                string path = LadderColorSettingsPath();
                if (!string.IsNullOrEmpty(path))
                    File.WriteAllText(path, ladderBaseColor.R + "," + ladderBaseColor.G + "," + ladderBaseColor.B);
            }
            catch { }
        }

        private void LoadLadderBackgroundColor()
        {
            try
            {
                string path = LadderColorSettingsPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string[] p = File.ReadAllText(path).Trim().Split(',');
                byte r, g, b;
                if (p.Length == 3 && byte.TryParse(p[0], out r) && byte.TryParse(p[1], out g) && byte.TryParse(p[2], out b))
                    ladderBaseColor = Color.FromRgb(r, g, b);
            }
            catch { }
        }

        private void ShowLadderBackgroundColorDialog()
        {
            Window dlg = new Window
            {
                Title = "Cor de fundo da ladder",
                Width = 360,
                Height = 285,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(43, 43, 46))
            };

            StackPanel panel = new StackPanel { Margin = new Thickness(16) };
            Border preview = new Border { Height = 48, Margin = new Thickness(0, 0, 0, 12), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            panel.Children.Add(preview);

            Slider r = CreateColorSlider("Vermelho", ladderBaseColor.R, panel);
            Slider g = CreateColorSlider("Verde", ladderBaseColor.G, panel);
            Slider b = CreateColorSlider("Azul", ladderBaseColor.B, panel);

            Action refreshPreview = () => preview.Background = new SolidColorBrush(Color.FromRgb((byte)r.Value, (byte)g.Value, (byte)b.Value));
            r.ValueChanged += (s, e) => refreshPreview();
            g.ValueChanged += (s, e) => refreshPreview();
            b.ValueChanged += (s, e) => refreshPreview();
            refreshPreview();

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            Button cancel = new Button { Content = "Cancelar", Width = 85, Height = 28, Margin = new Thickness(4, 0, 0, 0) };
            Button apply = new Button { Content = "Aplicar", Width = 85, Height = 28, Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.Bold };
            cancel.Click += (s, e) => dlg.Close();
            apply.Click += (s, e) => { SetLadderBackgroundColor(Color.FromRgb((byte)r.Value, (byte)g.Value, (byte)b.Value), true); dlg.Close(); };
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.ShowDialog();
        }

        private Slider CreateColorSlider(string label, byte value, Panel parent)
        {
            Grid row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            TextBlock name = new TextBlock { Text = label, Foreground = Brushes.WhiteSmoke, VerticalAlignment = VerticalAlignment.Center };
            Slider slider = new Slider { Minimum = 0, Maximum = 255, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(6, 0, 6, 0) };
            TextBlock number = new TextBlock { Text = value.ToString(), Foreground = Brushes.WhiteSmoke, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            // V0.9.9.77 - controles +/- desenhados manualmente com Border + TextBlock.
            // Isso evita o template de Button do NinjaTrader, que estava ocultando os caracteres.
            Border minus = new Border
            {
                Width = 26, Height = 24, Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(Color.FromRgb(48, 48, 52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(78, 78, 82)),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = "−", Foreground = Brushes.White, FontFamily = new FontFamily("Arial"),
                    FontSize = 18, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
            Border plus = new Border
            {
                Width = 26, Height = 24, Margin = new Thickness(2, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(48, 48, 52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(78, 78, 82)),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = "+", Foreground = Brushes.White, FontFamily = new FontFamily("Arial"),
                    FontSize = 18, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };

            slider.ValueChanged += (s, e) => number.Text = ((int)slider.Value).ToString();
            minus.MouseLeftButtonUp += (s, e) => { if (slider.Value > slider.Minimum) slider.Value -= 1; e.Handled = true; };
            plus.MouseLeftButtonUp += (s, e) => { if (slider.Value < slider.Maximum) slider.Value += 1; e.Handled = true; };

            Grid.SetColumn(name, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(number, 2);
            Grid.SetColumn(minus, 3);
            Grid.SetColumn(plus, 4);
            row.Children.Add(name);
            row.Children.Add(slider);
            row.Children.Add(number);
            row.Children.Add(minus);
            row.Children.Add(plus);
            parent.Children.Add(row);
            return slider;
        }

        private void AtmDiagTimer_Tick(object sender, EventArgs e)
        {
            if (atmStrategySelector == null || quantitySelector == null)
                return;

            NinjaTrader.NinjaScript.AtmStrategy selectedAtm = atmStrategySelector.SelectedAtmStrategy;

            if (selectedAtm == null)
            {
                lastAutoQtyAtm = null;
                return;
            }

            // IMPORTANTE:
            // O timer apenas DETECTA a troca da ATM. A quantidade é escrita UMA VEZ
            // por nova seleção, evitando reescrever QTD/LinkedQuantity durante
            // criação, envio ou gerenciamento da ordem ATM.
            if (!object.ReferenceEquals(selectedAtm, lastAutoQtyAtm))
            {
                lastAutoQtyAtm = selectedAtm;

                int atmQty = selectedAtm.EntryQuantity;
                if (atmQty > 0 && quantitySelector.Value != atmQty)
                    quantitySelector.Value = atmQty;
            }

            if (Caption != "Guardian DOM")
                Caption = "Guardian DOM";
        }

        private void PreviewOrderAtRow(int rowIndex, string side)
        {
            if (currentInstrument == null || rowIndex < 0 || rowIndex >= LadderRows)
                return;

            double price;
            if (priceCells[rowIndex].Tag is double)
                price = (double)priceCells[rowIndex].Tag;
            else if (!double.TryParse(priceCells[rowIndex].Text,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out price))
                return;

            int quantity = 1;
            if (quantitySelector != null && quantitySelector.Value > 0)
                quantity = (int)quantitySelector.Value;

            previewOrderPrice = currentInstrument.MasterInstrument.RoundToTickSize(price);
            previewOrderQuantity = quantity;
            previewOrderSide = side;

            // Regra de prévia:
            // COMPRA abaixo/na oferta atual -> LIMIT
            // COMPRA acima da oferta atual -> STOP MARKET
            // VENDA acima/no bid atual -> LIMIT
            // VENDA abaixo do bid atual -> STOP MARKET
            if (side == "COMPRA")
                previewOrderType = (!double.IsNaN(askPrice) && askPrice > 0 && previewOrderPrice > askPrice)
                    ? "STOP MARKET" : "LIMIT";
            else
                previewOrderType = (!double.IsNaN(bidPrice) && bidPrice > 0 && previewOrderPrice < bidPrice)
                    ? "STOP MARKET" : "LIMIT";

            UpdateOrderPreviewVisual();
            UpdateSendButtonState();
        }

        private void UpdateSendButtonState()
        {
            bool guardianHasLiveOrder =
                guardianSubmittedOrder != null &&
                guardianSubmittedOrder.OrderState != OrderState.Cancelled &&
                guardianSubmittedOrder.OrderState != OrderState.Filled &&
                guardianSubmittedOrder.OrderState != OrderState.Rejected;

            if (guardianHasLiveOrder)
            {
                if (sendPreviewButton != null)
                    sendPreviewButton.IsEnabled = false;
                return;
            }

            if (sendPreviewButton == null)
                return;

            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;
            bool accountReady = account != null;
            bool ready = currentInstrument != null
                && !double.IsNaN(previewOrderPrice)
                && previewOrderQuantity > 0
                && atmStrategySelector != null
                && atmStrategySelector.SelectedAtmStrategy != null
                && (previewOrderType == "LIMIT" || previewOrderType == "STOP MARKET");

            sendPreviewButton.IsEnabled = accountReady && ready;
        }

        // V0.9.9.65 - entrada imediata a mercado usando a ATM nativa selecionada.
        private void SubmitMarketEntry(bool buy)
        {
            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;
            if (account == null)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • SELECIONE UMA CONTA";
                return;
            }

            if (currentInstrument == null)
                return;

            NinjaTrader.NinjaScript.AtmStrategy selectedAtm =
                atmStrategySelector == null ? null : atmStrategySelector.SelectedAtmStrategy;
            if (selectedAtm == null)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • SELECIONE UMA ESTRATÉGIA ATM";
                return;
            }

            int quantity = 1;
            if (quantitySelector != null && quantitySelector.Value > 0)
                quantity = (int)quantitySelector.Value;

            try
            {
                guardianEntrySignalName = "Entry";
                Order order = account.CreateOrder(
                    currentInstrument,
                    buy ? OrderAction.Buy : OrderAction.SellShort,
                    OrderType.Market,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    quantity,
                    0,
                    0,
                    string.Empty,
                    guardianEntrySignalName,
                    Core.Globals.MaxDate,
                    null);

                guardianSubmittedOrder = null;
                guardianStopOrder = null;
                guardianTargetOrder = null;
                bracketSubmittedForEntry = false;
                beMonitorActive = false;
                guardianCancelRequested = false;

                if (guardianOrderAccount != account)
                {
                    if (guardianOrderAccount != null)
                        guardianOrderAccount.OrderUpdate -= GuardianOrderAccount_OrderUpdate;
                    guardianOrderAccount = account;
                    guardianOrderAccount.OrderUpdate += GuardianOrderAccount_OrderUpdate;
                }

                guardianSubmittedOrder = order;
                guardianOrderStatus = "SUBMIT CALLED";
                NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(selectedAtm, order);

                if (orderStateStatus != null)
                {
                    orderStateStatus.Text = "ORDEM: " + (buy ? "COMPRA" : "VENDA") + " MERCADO • ENVIO SOLICITADO";
                    orderStateStatus.Foreground = Brushes.Gold;
                }
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • " +
                    (buy ? "COMPRA" : "VENDA") + " MERCADO " + quantity + " • ATM • " + account.Name;
            }
            catch (Exception ex)
            {
                guardianSubmittedOrder = null;
                guardianOrderStatus = "ERRO";
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • ERRO AO ENVIAR MERCADO: " + ex.Message;
            }
        }

        // V0.9.9.65 - fechamento do ativo selecionado.
        // Account.Flatten cancela ordens de trabalho do instrumento e zera a posição,
        // evitando deixar STOP/ALVO da ATM órfãos após o fechamento manual.
        private void FlattenButton_Click(object sender, RoutedEventArgs e)
        {
            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;
            if (account == null)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • SELECIONE UMA CONTA";
                return;
            }
            if (currentInstrument == null)
                return;

            try
            {
                account.Flatten(new Instrument[] { currentInstrument });
                guardianOrderStatus = "FECHAMENTO SOLICITADO";
                if (orderStateStatus != null)
                {
                    orderStateStatus.Text = "ORDEM: FECHAMENTO SOLICITADO";
                    orderStateStatus.Foreground = Brushes.Gold;
                }
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • FECHAMENTO SOLICITADO • " +
                    currentInstrument.FullName + " • " + account.Name;
            }
            catch (Exception ex)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • ERRO NO FECHAMENTO: " + ex.Message;
            }
        }

        // GDOM128 - envio para a conta atualmente selecionada no GuardianDOM.
        // A entrada usa a ATM nativa selecionada e a conta escolhida no seletor CONTA.
        private void SendPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;

            if (account == null)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • SELECIONE UMA CONTA";
                return;
            }

            if (currentInstrument == null || double.IsNaN(previewOrderPrice) || previewOrderQuantity <= 0)
                return;

            NinjaTrader.NinjaScript.AtmStrategy selectedAtm =
                atmStrategySelector == null ? null : atmStrategySelector.SelectedAtmStrategy;

            if (selectedAtm == null)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • SELECIONE UMA ESTRATÉGIA ATM";
                UpdateSendButtonState();
                return;
            }

            OrderAction action = previewOrderSide == "COMPRA"
                ? OrderAction.Buy
                : OrderAction.SellShort;

            OrderType type = previewOrderType == "STOP MARKET"
                ? OrderType.StopMarket
                : OrderType.Limit;

            double limitPrice = type == OrderType.Limit ? previewOrderPrice : 0;
            double stopPrice  = type == OrderType.StopMarket ? previewOrderPrice : 0;

            try
            {
                // V0.9.9.58: requisito crítico da API StartAtmStrategy.
                // Para uma entrada associada a ATM, o parâmetro name do CreateOrder
                // DEVE ser exatamente "Entry". Qualquer outro nome pode deixar a ordem
                // apenas em Initialized e impedir a submissão efetiva.
                guardianEntrySignalName = "Entry";

                Order order = account.CreateOrder(
                    currentInstrument,
                    action,
                    type,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    previewOrderQuantity,
                    limitPrice,
                    stopPrice,
                    string.Empty,
                    guardianEntrySignalName,
                    Core.Globals.MaxDate,
                    null);

                // Limpa referências da entrada anterior antes de iniciar uma nova.
                guardianSubmittedOrder = null;
                guardianStopOrder = null;
                guardianTargetOrder = null;
                bracketSubmittedForEntry = false;
                beMonitorActive = false;
                be20Detected = false;
                be1ChangeSent = false;
                be40Detected = false;
                beEntryFillPrice = double.NaN;
                beEntryDirection = 0;

                // Assina os eventos ANTES do StartAtmStrategy para não perder
                // atualizações rápidas de Accepted/Working/Fill.
                if (guardianOrderAccount != account)
                {
                    if (guardianOrderAccount != null)
                        guardianOrderAccount.OrderUpdate -= GuardianOrderAccount_OrderUpdate;

                    guardianOrderAccount = account;
                    guardianOrderAccount.OrderUpdate += GuardianOrderAccount_OrderUpdate;
                }

                guardianCancelRequested = false;
                guardianSubmittedOrder = order;

                // V0.9.9.58 - diagnóstico do ponto exato de submissão.
                // Mostra o estado devolvido por CreateOrder ANTES de entregar a ordem à ATM.
                guardianOrderStatus = "CREATE OK";
                if (orderStateStatus != null)
                {
                    orderStateStatus.Text = "DIAG: CREATE OK • STATE=" + order.OrderState.ToString().ToUpperInvariant();
                    orderStateStatus.Foreground = Brushes.Gold;
                }

                // A ATM nativa escolhida deve efetivamente submeter a entrada.
                NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(selectedAtm, order);

                // Se chegamos aqui, StartAtmStrategy retornou sem exceção. Não chamamos isso
                // de ENVIADA: Accepted/Working/Rejected continua dependendo de OrderUpdate.
                guardianOrderStatus = "SUBMIT CALLED";

                string diagOrderId = string.IsNullOrEmpty(order.OrderId) ? "SEM ID" : order.OrderId;
                if (orderStateStatus != null)
                {
                    orderStateStatus.Text =
                        "DIAG: CREATE OK > SUBMIT CALLED • STATE=" +
                        order.OrderState.ToString().ToUpperInvariant() +
                        " • ID=" + diagOrderId;
                    orderStateStatus.Foreground = Brushes.Gold;
                }

                if (cancelOrderButton != null)
                {
                    cancelOrderButton.Content = "CANCELAR ORDEM DO GUARDIANDOM";
                    cancelOrderButton.IsEnabled = false;
                }

                connectionStatus.Text =
                    "V0.9.9.89 PERÍODO GRÁFICO • ENVIO SOLICITADO: " + previewOrderSide +
                    " " + previewOrderQuantity + " @ " +
                    currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice) +
                    " • " + previewOrderType + " • SUBMIT DIAGNOSTIC • " + account.Name;

                sendPreviewButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                guardianSubmittedOrder = null;
                guardianOrderStatus = "ERRO";

                if (orderStateStatus != null)
                {
                    orderStateStatus.Text = "ORDEM: ERRO NO ENVIO";
                    orderStateStatus.Foreground = Brushes.OrangeRed;
                }

                connectionStatus.Text =
                    "V0.9.9.89 PERÍODO GRÁFICO • ERRO AO ENVIAR: " + ex.Message;

                UpdateSendButtonState();
            }
        }

        // V0.9.9.52 - cancelamento da entrada ATM pela mesma Order enviada.
        // V0.9.9.53 - cancelamento robusto: procura a ordem de entrada REAL/ATIVA
        // na coleção da conta, pois StartAtmStrategy pode substituir a referência original.
        // V0.9.9.54 - não libera/limpa a ordem no clique; aguarda confirmação terminal.
        // V0.9.9.56 - signal name estável para compatibilidade com a ATM; a referência oficial
        // é adotada somente a partir do fluxo real de OrderUpdate desta conta.
        private bool IsGuardianEntryOrder(Order order)
        {
            if (order == null || currentInstrument == null)
                return false;

            bool sameInstrument = order.Instrument != null &&
                string.Equals(order.Instrument.FullName, currentInstrument.FullName, StringComparison.OrdinalIgnoreCase);

            bool guardianName = !string.IsNullOrEmpty(order.Name) &&
                !string.IsNullOrEmpty(guardianEntrySignalName) &&
                string.Equals(order.Name, guardianEntrySignalName, StringComparison.OrdinalIgnoreCase);

            return sameInstrument && guardianName;
        }

        private bool IsOrderCancellable(Order order)
        {
            if (order == null)
                return false;

            return order.OrderState == OrderState.Accepted ||
                   order.OrderState == OrderState.Working ||
                   order.OrderState == OrderState.TriggerPending ||
                   order.OrderState == OrderState.ChangePending ||
                   order.OrderState == OrderState.Submitted;
        }

        private void CancelOrderButton_Click(object sender, RoutedEventArgs e)
        {
            Account account = guardianOrderAccount;

            if (account == null)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • SELECIONE UMA CONTA";
                return;
            }

            try
            {
                List<Order> toCancel = new List<Order>();

                // Primeiro tenta a referência conhecida, se ainda for realmente cancelável.
                if (IsOrderCancellable(guardianSubmittedOrder))
                    toCancel.Add(guardianSubmittedOrder);

                // Depois procura a ordem efetivamente registrada na conta.
                // Isso cobre a troca de referência/ID feita pelo StartAtmStrategy.
                foreach (Order candidate in account.Orders)
                {
                    if (!IsGuardianEntryOrder(candidate) || !IsOrderCancellable(candidate))
                        continue;

                    bool alreadyAdded = false;
                    foreach (Order existing in toCancel)
                    {
                        if (IsSameOrder(existing, candidate))
                        {
                            alreadyAdded = true;
                            break;
                        }
                    }

                    if (!alreadyAdded)
                        toCancel.Add(candidate);

                    // Passa a acompanhar a ordem REAL vista pela conta.
                    guardianSubmittedOrder = candidate;
                }

                if (toCancel.Count == 0)
                {
                    // V0.9.9.54: NÃO limpa a ordem por ausência momentânea de uma
                    // referência cancelável. Se já houve solicitação, aguardamos a
                    // confirmação terminal (Cancelled/Filled/Rejected) via OrderUpdate.
                    if (guardianCancelRequested ||
                        (guardianSubmittedOrder != null && guardianSubmittedOrder.OrderState == OrderState.CancelPending))
                    {
                        guardianCancelRequested = true;
                        guardianOrderStatus = "CANCELANDO";
                        if (orderStateStatus != null)
                        {
                            orderStateStatus.Text = "ORDEM: CANCELANDO • AGUARDANDO CONFIRMAÇÃO";
                            orderStateStatus.Foreground = Brushes.Gold;
                        }
                        if (cancelOrderButton != null)
                            cancelOrderButton.IsEnabled = false;
                        if (sendPreviewButton != null)
                            sendPreviewButton.IsEnabled = false;
                        connectionStatus.Text =
                            "V0.9.9.89 PERÍODO GRÁFICO • CANCELAMENTO PENDENTE • AGUARDANDO NINJATRADER";
                        return;
                    }

                    guardianOrderStatus = "SEM ORDEM CANCELÁVEL";
                    if (orderStateStatus != null)
                    {
                        orderStateStatus.Text = "ORDEM: SEM ORDEM CANCELÁVEL";
                        orderStateStatus.Foreground = Brushes.White;
                    }
                    UpdateSendButtonState();
                    return;
                }

                // A partir daqui o GuardianDOM entra em estado de cancelamento e
                // permanece bloqueado até o OrderUpdate confirmar um estado terminal.
                guardianCancelRequested = true;
                account.Cancel(toCancel.ToArray());

                guardianOrderStatus = "CANCELANDO";
                if (orderStateStatus != null)
                {
                    orderStateStatus.Text = "ORDEM: CANCELANDO";
                    orderStateStatus.Foreground = Brushes.Gold;
                }
                if (cancelOrderButton != null)
                    cancelOrderButton.IsEnabled = false;

                connectionStatus.Text =
                    "V0.9.9.89 PERÍODO GRÁFICO • CANCELANDO " +
                    toCancel.Count + " ORDEM(NS) GUARDIANDOM • " + account.Name;
            }
            catch (Exception ex)
            {
                guardianOrderStatus = "ERRO CANCELAMENTO";
                if (orderStateStatus != null)
                {
                    orderStateStatus.Text = "ORDEM: ERRO AO CANCELAR";
                    orderStateStatus.Foreground = Brushes.OrangeRed;
                }
                if (cancelOrderButton != null)
                    cancelOrderButton.IsEnabled = true;

                connectionStatus.Text =
                    "V0.9.9.89 PERÍODO GRÁFICO • ERRO AO CANCELAR: " + ex.Message;
            }
        }

        private void SubmitProtectiveBracket(Order filledEntry)
        {
            if (filledEntry == null || guardianOrderAccount == null || bracketSubmittedForEntry)
                return;

            double fillPrice = filledEntry.AverageFillPrice;
            int qty = filledEntry.Filled > 0 ? filledEntry.Filled : filledEntry.Quantity;
            if (fillPrice <= 0 || qty <= 0)
                return;

            double tick = filledEntry.Instrument.MasterInstrument.TickSize;
            bool longEntry = filledEntry.OrderAction == OrderAction.Buy;
            bool shortEntry = filledEntry.OrderAction == OrderAction.SellShort;

            if (!longEntry && !shortEntry)
                return;

            double stopPrice = longEntry
                ? fillPrice - ProtectiveStopTicks * tick
                : fillPrice + ProtectiveStopTicks * tick;
            double targetPrice = longEntry
                ? fillPrice + ProtectiveTargetTicks * tick
                : fillPrice - ProtectiveTargetTicks * tick;

            stopPrice = filledEntry.Instrument.MasterInstrument.RoundToTickSize(stopPrice);
            targetPrice = filledEntry.Instrument.MasterInstrument.RoundToTickSize(targetPrice);

            OrderAction exitAction = longEntry ? OrderAction.Sell : OrderAction.BuyToCover;
            string oco = "GuardianDOM-" + Guid.NewGuid().ToString("N");

            guardianStopOrder = guardianOrderAccount.CreateOrder(
                filledEntry.Instrument,
                exitAction,
                OrderType.StopMarket,
                OrderEntry.Manual,
                TimeInForce.Day,
                qty,
                0,
                stopPrice,
                oco,
                "GuardianDOM Stop",
                Core.Globals.MaxDate,
                null);

            guardianTargetOrder = guardianOrderAccount.CreateOrder(
                filledEntry.Instrument,
                exitAction,
                OrderType.Limit,
                OrderEntry.Manual,
                TimeInForce.Day,
                qty,
                targetPrice,
                0,
                oco,
                "GuardianDOM Target",
                Core.Globals.MaxDate,
                null);

            // Marca antes do Submit para impedir duplicidade se eventos chegarem imediatamente.
            bracketSubmittedForEntry = true;
            guardianOrderAccount.Submit(new[] { guardianStopOrder, guardianTargetOrder });

            // Diagnóstico: somente observa +20/+40. Não altera qualquer ordem.
            beEntryFillPrice = fillPrice;
            beEntryDirection = longEntry ? 1 : -1;
            be20Detected = false;
            be1ChangeSent = false;
            be40Detected = false;
            beMonitorActive = true;

            if (orderStateStatus != null)
            {
                orderStateStatus.Text =
                    "POSIÇÃO PROTEGIDA • STOP " + ProtectiveStopTicks +
                    "t • ALVO " + ProtectiveTargetTicks + "t";
                orderStateStatus.Foreground = Brushes.LimeGreen;
            }

            connectionStatus.Text =
                "V0.9.9.89 PERÍODO GRÁFICO • STOP " +
                filledEntry.Instrument.MasterInstrument.FormatPrice(stopPrice) +
                " • ALVO " +
                filledEntry.Instrument.MasterInstrument.FormatPrice(targetPrice) +
                " • OCO • " + guardianOrderAccount.Name;
        }

        private bool IsSameOrder(Order a, Order b)
        {
            if (a == null || b == null)
                return false;

            if (object.ReferenceEquals(a, b))
                return true;

            return !string.IsNullOrEmpty(a.OrderId)
                && !string.IsNullOrEmpty(b.OrderId)
                && string.Equals(a.OrderId, b.OrderId, StringComparison.OrdinalIgnoreCase);
        }

        private void GuardianOrderAccount_OrderUpdate(object sender, OrderEventArgs e)
        {
            if (e == null || e.Order == null)
                return;

            bool isEntry = IsSameOrder(e.Order, guardianSubmittedOrder);
            bool isStop = IsSameOrder(e.Order, guardianStopOrder);
            bool isTarget = IsSameOrder(e.Order, guardianTargetOrder);

            // V0.9.9.56: StartAtmStrategy pode publicar uma nova instância/ID da entrada.
            // Só o OrderUpdate transforma o pedido de envio em uma ordem confirmada.
            if (!isEntry && IsGuardianEntryOrder(e.Order))
            {
                // V0.9.9.54: ordens GuardianDOM antigas já Cancelled/Rejected não
                // podem "roubar" o rastreamento da entrada atual. Só adotamos uma
                // nova instância publicada pela ATM enquanto ela ainda está ativa,
                // ou se ela acabou de ser executada.
                bool adoptable = IsOrderCancellable(e.Order) ||
                    e.Order.OrderState == OrderState.CancelPending ||
                    e.Order.OrderState == OrderState.Filled;

                if (adoptable)
                {
                    guardianSubmittedOrder = e.Order;
                    isEntry = true;
                }
            }

            // Ignora qualquer ordem que não pertença a esta instância do GuardianDOM.
            if (!isEntry && !isStop && !isTarget)
                return;

            // V0.9.9.0: a proteção pertence à ATM nativa selecionada.
            // Não cria bracket próprio do GuardianDOM.
            if (isEntry && e.Order.OrderState == OrderState.Filled)
            {
                bracketSubmittedForEntry = true;
                beMonitorActive = false;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                OrderState state = e.Order.OrderState;

                // Atualizações do STOP/ALVO têm prioridade visual após a entrada.
                if (isStop || isTarget)
                {
                    if (state == OrderState.Filled)
                    {
                        guardianOrderStatus = isStop ? "STOP EXECUTADO" : "ALVO EXECUTADO";

                        if (orderStateStatus != null)
                        {
                            orderStateStatus.Text = isStop
                                ? "POSIÇÃO ENCERRADA • STOP EXECUTADO"
                                : "POSIÇÃO ENCERRADA • ALVO EXECUTADO";
                            orderStateStatus.Foreground = isStop ? Brushes.Gold : Brushes.LimeGreen;
                        }

                        connectionStatus.Text = isStop
                            ? "V0.9.9.89 PERÍODO GRÁFICO • POSIÇÃO ENCERRADA PELO STOP • OCO"
                            : "V0.9.9.89 PERÍODO GRÁFICO • POSIÇÃO ENCERRADA PELO ALVO • OCO";
                    }
                    else if (state == OrderState.Rejected)
                    {
                        if (orderStateStatus != null)
                        {
                            orderStateStatus.Text = isStop ? "STOP REJEITADO" : "ALVO REJEITADO";
                            orderStateStatus.Foreground = Brushes.OrangeRed;
                        }
                        connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • ORDEM DE PROTEÇÃO REJEITADA";
                    }

                    // Cancelled no irmão OCO não deve sobrescrever a mensagem de saída executada.
                    return;
                }

                // Daqui para baixo: somente a ordem de entrada.
                guardianOrderStatus = state.ToString();

                bool cancellable =
                    state == OrderState.Accepted ||
                    state == OrderState.Working ||
                    state == OrderState.TriggerPending ||
                    state == OrderState.ChangePending;

                if (cancelOrderButton != null)
                    cancelOrderButton.IsEnabled = cancellable && !guardianCancelRequested;

                // Enquanto o NinjaTrader ainda não confirmou o cancelamento,
                // também não permitimos um segundo envio.
                if (guardianCancelRequested && sendPreviewButton != null)
                    sendPreviewButton.IsEnabled = false;

                string statePt;
                switch (state)
                {
                    case OrderState.Accepted:
                    case OrderState.Working:
                        statePt = "PENDENTE";
                        break;
                    case OrderState.Filled:
                        statePt = bracketSubmittedForEntry ? "EXECUTADA + ATM" : "EXECUTADA";
                        break;
                    case OrderState.Cancelled:
                        statePt = "CANCELADA";
                        break;
                    case OrderState.Rejected:
                        statePt = "REJEITADA";
                        break;
                    case OrderState.CancelPending:
                        statePt = "CANCELANDO";
                        break;
                    default:
                        statePt = state.ToString().ToUpperInvariant();
                        break;
                }

                if (orderStateStatus != null)
                {
                    orderStateStatus.Text =
                        (state == OrderState.Filled && bracketSubmittedForEntry)
                        ? "POSIÇÃO PROTEGIDA • ATM ATIVA"
                        : "ORDEM: " + statePt;

                    if (state == OrderState.Filled)
                        orderStateStatus.Foreground = Brushes.LimeGreen;
                    else if (state == OrderState.Rejected)
                        orderStateStatus.Foreground = Brushes.OrangeRed;
                    else if (state == OrderState.Cancelled)
                        orderStateStatus.Foreground = Brushes.Gold;
                    else
                        orderStateStatus.Foreground = Brushes.White;
                }

                // Somente uma confirmação terminal do NinjaTrader encerra o
                // ciclo de cancelamento. O clique no botão, sozinho, nunca libera
                // a interface nem apaga a referência da ordem.
                if (state == OrderState.Cancelled ||
                    state == OrderState.Filled ||
                    state == OrderState.Rejected)
                {
                    guardianCancelRequested = false;
                    UpdateSendButtonState();
                }

                connectionStatus.Text =
                    "V0.9.9.89 PERÍODO GRÁFICO • ORDEM: " + statePt +
                    " • " + previewOrderSide + " " + previewOrderQuantity +
                    " @ " + (currentInstrument == null ? "--" :
                        currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice)) +
                    " • " + (guardianOrderAccount == null ? "--" : guardianOrderAccount.Name);
            }));
        }

        private void UpdateOrderPreviewVisual()
        {
            for (int i = 0; i < LadderRows; i++)
            {
                priceBorders[i].BorderBrush =
                    new SolidColorBrush(Color.FromRgb(58, 58, 61));
                // GDOM117 - mantém a divisória horizontal de 1 px em TODOS os níveis de preço.
                // Antes, a atualização da prévia sobrescrevia a borda fixa com 0,5 px,
                // fazendo algumas linhas desaparecerem visualmente. Cores preservadas da GDOM116.
                priceBorders[i].BorderThickness = new Thickness(0, 0, 1, 1);
            }

            if (double.IsNaN(previewOrderPrice) || currentInstrument == null)
                return;

            double tick = currentInstrument.MasterInstrument.TickSize;

            for (int i = 0; i < LadderRows; i++)
            {
                double rowPrice;
                if (priceCells[i].Tag is double)
                    rowPrice = (double)priceCells[i].Tag;
                else if (!double.TryParse(priceCells[i].Text,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out rowPrice))
                    continue;

                if (Math.Abs(rowPrice - previewOrderPrice) < tick * 0.5)
                {
                    priceBorders[i].BorderBrush =
                        previewOrderSide == "COMPRA" ? Brushes.LimeGreen : Brushes.IndianRed;
                    priceBorders[i].BorderThickness = new Thickness(2);
                    break;
                }
            }

            connectionStatus.Text =
                "V0.9.9.89 PERÍODO GRÁFICO • " + previewOrderSide +
                " " + previewOrderQuantity.ToString() +
                " @ " + currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice) +
                " • " + previewOrderType +
                " • PRÉVIA PRONTA • CONTA SELECIONADA";
        }

        private void OnFastPriceRefreshTimerTick(object sender, EventArgs e)
        {
            if (currentInstrument == null)
                return;

            // Processa imediatamente os eventos pendentes para que os preços
            // não esperem o ciclo pesado de 100 ms.
            DrainPendingMarketEvents();

            // Atualização propositalmente leve: não recalcula perfil, barras,
            // volume nem reconstrói a ladder.
            if (bidValue != null)
                bidValue.Text = FormatPrice(bidPrice);
            if (askValue != null)
                askValue.Text = FormatPrice(askPrice);
            if (lastValue != null)
                lastValue.Text = FormatPrice(lastPrice);
        }

        private void OnDepthRefreshTimerTick(object sender, EventArgs e)
        {
            if (currentInstrument == null)
                return;

            // V0.9.9.40 PERFORMANCE:
            // processamento de mercado continua acumulando todos os negócios,
            // mas perfil + interface são consolidados no timer (10 Hz).
            // Evita redesenhar toda a ladder a cada evento de market data.
            DrainPendingMarketEvents();
            TrimTimedFlowTrades();
            RefreshDailyProfile();
            RecalculateDailyProfile();
            UpdateDisplay();
        }

        private void OnInstrumentChanged(object sender, EventArgs e)
        {
            Instrument selected = instrumentSelector != null ? instrumentSelector.Instrument : null;

            // V0.9.9.83 - grava somente selecoes validas.
            // Assim, fechar/reabrir a janela nao substitui o ultimo ativo por "Selecionar".
            if (selected != null)
                SaveLastInstrument(selected);

            ChangeInstrument(selected);

            if (selected != null && !receivingLinkedInstrument)
                PropagateGuardianInstrument(selected);
        }

        // V0.9.9.94 - sincroniza apenas o ATIVO entre Guardian DOMs da mesma cor.
        private void PropagateGuardianInstrument(Instrument instrument)
        {
            if (instrument == null || string.IsNullOrWhiteSpace(instrumentLinkGroup) ||
                string.Equals(instrumentLinkGroup, "Nenhum", StringComparison.OrdinalIgnoreCase))
                return;

            List<GuardianDomWindow> targets;
            lock (guardianLinkSync)
                targets = new List<GuardianDomWindow>(guardianLinkWindows);

            foreach (GuardianDomWindow target in targets)
            {
                if (target == null || ReferenceEquals(target, this) ||
                    !string.Equals(target.instrumentLinkGroup, instrumentLinkGroup, StringComparison.OrdinalIgnoreCase))
                    continue;

                target.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (target.instrumentSelector == null)
                            return;

                        Instrument current = target.instrumentSelector.Instrument;
                        if (current != null && string.Equals(current.FullName, instrument.FullName, StringComparison.OrdinalIgnoreCase))
                            return;

                        target.receivingLinkedInstrument = true;
                        target.startupInstrumentName = instrument.FullName;
                        target.instrumentSelector.Instrument = instrument;
                    }
                    finally
                    {
                        target.receivingLinkedInstrument = false;
                    }
                }));
            }
        }

        private string GetInstrumentLinkPath()
        {
            try { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GuardianDOM_InstrumentLink.txt"); }
            catch { return string.Empty; }
        }

        private string ReadInstrumentLinkGroup()
        {
            try
            {
                string path = GetInstrumentLinkPath();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string value = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            catch { }
            return "Nenhum";
        }

        private void SaveInstrumentLinkGroup()
        {
            try
            {
                string path = GetInstrumentLinkPath();
                if (!string.IsNullOrEmpty(path))
                    File.WriteAllText(path, instrumentLinkGroup ?? "Nenhum");
            }
            catch { }
        }

        private Brush GuardianLinkBrush(string name)
        {
            switch (name)
            {
                case "Vermelho": return Brushes.Red;
                case "Laranja": return Brushes.Orange;
                case "Amarelo": return Brushes.Yellow;
                case "Ouro": return Brushes.Gold;
                case "Lima": return Brushes.Lime;
                case "Verde": return Brushes.Green;
                case "Aqua": return Brushes.Aqua;
                case "Azul": return Brushes.Blue;
                case "Dodger": return Brushes.DodgerBlue;
                case "Violeta": return Brushes.Violet;
                case "Roxo": return Brushes.Purple;
                case "Rosa": return Brushes.Pink;
                default: return Brushes.Transparent;
            }
        }

        private void GuardianDomWindow_LoadedInstallTitleLink(object sender, RoutedEventArgs e)
        {
            // V0.9.9.102 - tenta instalar imediatamente no Loaded.
            // Se o template nativo ainda estiver concluindo o primeiro layout, a própria
            // rotina agenda uma nova tentativa em prioridade Render (antes de Idle).
            try { InstallTitleInstrumentLinkButton(); }
            catch { }
        }

        private FrameworkElement FindMinimizeTitleElement(DependencyObject root)
        {
            if (root == null)
                return null;

            FrameworkElement fe = root as FrameworkElement;
            if (fe != null)
            {
                string name = fe.Name ?? string.Empty;
                string tip = fe.ToolTip != null ? fe.ToolTip.ToString() : string.Empty;
                string content = string.Empty;
                ContentControl cc = fe as ContentControl;
                if (cc != null && cc.Content != null)
                    content = cc.Content.ToString();

                string probe = (name + " " + tip + " " + content).ToLowerInvariant();
                if (probe.Contains("minimiz") || probe.Contains("minimize"))
                    return fe;
            }

            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(root); }
            catch { return null; }

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                FrameworkElement found = FindMinimizeTitleElement(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        private Panel FindNativeTitleButtonHost(FrameworkElement minimizeElement)
        {
            if (minimizeElement == null)
                return null;

            DependencyObject current = minimizeElement;
            while (current != null && !ReferenceEquals(current, this))
            {
                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(current); }
                catch { parent = null; }

                Panel panel = parent as Panel;
                if (panel != null && current is UIElement && panel.Children.Contains((UIElement)current))
                    return panel;

                current = parent;
            }
            return null;
        }

        private void InstallTitleInstrumentLinkButton()
        {
            if (titleButtonsInstalled)
                return;

            FrameworkElement minimizeElement = FindMinimizeTitleElement(this);
            Panel titleHost = FindNativeTitleButtonHost(minimizeElement);
            if (minimizeElement == null || titleHost == null)
            {
                // V0.9.9.102 - não espera ContextIdle. Render ocorre durante a
                // montagem visual inicial da janela, fazendo os botões aparecerem junto
                // dos controles nativos assim que o host da barra estiver disponível.
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    try
                    {
                        if (!titleButtonsInstalled)
                            InstallTitleInstrumentLinkButton();
                    }
                    catch { }
                }));
                return;
            }

            Button nativeTitleButton = minimizeElement as Button;
            double nativeButtonWidth = minimizeElement.ActualWidth > 0 ? minimizeElement.ActualWidth : 18;
            double nativeButtonHeight = minimizeElement.ActualHeight > 0 ? minimizeElement.ActualHeight : 18;

            titleInstrumentLinkButton = new Button
            {
                Width = nativeButtonWidth,
                Height = nativeButtonHeight,
                MinWidth = 0,
                MinHeight = 0,
                MaxWidth = nativeButtonWidth,
                MaxHeight = nativeButtonHeight,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 0),
                ToolTip = "Vincular ativo",
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // V0.9.9.101 - NÃO herda o Style do botão nativo no botão de vínculo.
            // O template nativo do NT força o Background e impedia a cor escolhida
            // de preencher a superfície do botão. Mantemos tamanho/alinhamento nativos,
            // mas o fundo deste botão fica sob controle do GuardianDOM.
            titleInstrumentLinkButton.Click += (s, e) =>
            {
                System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu();
                menu.Items.Add(CreateInstrumentLinkMenu());
                menu.PlacementTarget = titleInstrumentLinkButton;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
                e.Handled = true;
            };

            UpdateTitleInstrumentLinkButton();

            titleDuplicateWindowButton = new Button
            {
                Content = "⧉",
                Width = nativeButtonWidth,
                Height = nativeButtonHeight,
                MinWidth = 0,
                MinHeight = 0,
                MaxWidth = nativeButtonWidth,
                MaxHeight = nativeButtonHeight,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 0),
                Foreground = Brushes.WhiteSmoke,
                FontSize = 14,
                FontWeight = FontWeights.Normal,
                ToolTip = "Janela Duplicar",
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (nativeTitleButton != null)
                titleDuplicateWindowButton.Style = nativeTitleButton.Style;
            titleDuplicateWindowButton.Click += (s, e) =>
            {
                DuplicateGuardianDomWindow();
                e.Handled = true;
            };

            StackPanel titleButtonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            titleButtonsPanel.Children.Add(titleDuplicateWindowButton);
            titleButtonsPanel.Children.Add(titleInstrumentLinkButton);

            int minimizeIndex = titleHost.Children.IndexOf(minimizeElement);
            if (minimizeIndex < 0)
                return;

            // Faz os dois controles participarem da própria árvore visual da barra
            // de título do NTWindow. Não usa Popup/overlay.
            titleHost.Children.Insert(minimizeIndex, titleButtonsPanel);
            titleButtonsInstalled = true;
        }

        private void UpdateTitleInstrumentLinkButton()
        {
            if (titleInstrumentLinkButton == null)
                return;

            Brush linkBrush = GuardianLinkBrush(instrumentLinkGroup);
            bool linked = !string.Equals(instrumentLinkGroup, "Nenhum", StringComparison.OrdinalIgnoreCase);

            // V0.9.9.100 - a cor ocupa o próprio botão de vínculo.
            // Não desenha mais um quadradinho dentro do botão.
            titleInstrumentLinkButton.Content = null;
            titleInstrumentLinkButton.Background = linked ? linkBrush : new SolidColorBrush(Color.FromRgb(38, 38, 40));
            titleInstrumentLinkButton.BorderBrush = linked ? new SolidColorBrush(Color.FromRgb(125, 125, 125)) : new SolidColorBrush(Color.FromRgb(105, 105, 105));
            titleInstrumentLinkButton.BorderThickness = new Thickness(1);
            titleInstrumentLinkButton.Opacity = 1.0;
            titleInstrumentLinkButton.ToolTip = linked
                ? "Vincular ativo: " + instrumentLinkGroup
                : "Vincular ativo: Nenhum";
        }

        private MenuItem CreateInstrumentLinkMenu()
        {
            MenuItem root = new MenuItem { Header = "Vincular ativo" };
            string[] groups = new string[]
            {
                "Nenhum", "Vermelho", "Laranja", "Amarelo", "Ouro", "Lima",
                "Verde", "Aqua", "Azul", "Dodger", "Violeta", "Roxo", "Rosa"
            };

            foreach (string group in groups)
            {
                MenuItem item = new MenuItem
                {
                    Header = group,
                    IsCheckable = true,
                    IsChecked = string.Equals(instrumentLinkGroup, group, StringComparison.OrdinalIgnoreCase)
                };

                if (!string.Equals(group, "Nenhum", StringComparison.OrdinalIgnoreCase))
                {
                    Border swatch = new Border
                    {
                        Width = 11,
                        Height = 11,
                        Background = GuardianLinkBrush(group),
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(1)
                    };
                    item.Icon = swatch;
                }

                string selectedGroup = group;
                item.Click += (s, e) =>
                {
                    instrumentLinkGroup = selectedGroup;
                    SaveInstrumentLinkGroup();
                    UpdateTitleInstrumentLinkButton();

                    foreach (object child in root.Items)
                    {
                        MenuItem mi = child as MenuItem;
                        if (mi != null)
                            mi.IsChecked = string.Equals(mi.Header as string, selectedGroup, StringComparison.OrdinalIgnoreCase);
                    }

                    // Ao entrar em um grupo, publica imediatamente o ativo atual.
                    if (currentInstrument != null &&
                        !string.Equals(selectedGroup, "Nenhum", StringComparison.OrdinalIgnoreCase))
                        PropagateGuardianInstrument(currentInstrument);
                };

                root.Items.Add(item);
            }
            return root;
        }

        // V0.9.9.83 - persistencia simples e independente do ultimo ativo.
        // Segue o mesmo padrao dos arquivos de preferencias ja usados pelo DOM.
        private string GetLastInstrumentPath()
        {
            try { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GuardianDOM_LastInstrument.txt"); }
            catch { return string.Empty; }
        }

        private string ReadLastInstrumentName()
        {
            if (!string.IsNullOrWhiteSpace(lastInstrumentSessionName))
                return lastInstrumentSessionName;

            try
            {
                string path = GetLastInstrumentPath();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string name = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        lastInstrumentSessionName = name;
                        return name;
                    }
                }
            }
            catch { }

            return string.Empty;
        }

        private void SaveLastInstrument(Instrument instrument)
        {
            if (instrument == null || string.IsNullOrWhiteSpace(instrument.FullName))
                return;

            lastInstrumentSessionName = instrument.FullName;
            startupInstrumentName = instrument.FullName;

            try
            {
                string path = GetLastInstrumentPath();
                if (!string.IsNullOrEmpty(path))
                    File.WriteAllText(path, instrument.FullName);
            }
            catch
            {
                // A copia em memoria continua disponivel mesmo se a gravacao em disco falhar.
            }
        }

        private void GuardianDomWindow_LoadedRestoreInstrument(object sender, RoutedEventArgs e)
        {
            Loaded -= GuardianDomWindow_LoadedRestoreInstrument;

            // Agenda depois do Loaded para deixar o InstrumentSelector terminar
            // a inicializacao interna antes da primeira atribuicao.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                StartLastInstrumentRestore();
            }));
        }

        private void StartLastInstrumentRestore()
        {
            if (instrumentSelector == null)
                return;

            lastInstrumentRestoreAttempts = 0;
            lastInstrumentRestoreStableTicks = 0;

            if (lastInstrumentRestoreTimer != null)
            {
                lastInstrumentRestoreTimer.Stop();
                lastInstrumentRestoreTimer.Tick -= LastInstrumentRestoreTimer_Tick;
            }

            lastInstrumentRestoreTimer = new DispatcherTimer(DispatcherPriority.Loaded)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            lastInstrumentRestoreTimer.Tick += LastInstrumentRestoreTimer_Tick;

            // V0.9.9.86 - nao encerramos na primeira atribuicao bem-sucedida.
            // O NinjaTrader pode sobrescrever o InstrumentSelector alguns instantes depois.
            // Fazemos a primeira tentativa agora e mantemos o timer ate o ativo permanecer
            // correto por varios ciclos consecutivos.
            TryLoadLastInstrument();
            lastInstrumentRestoreTimer.Start();
        }

        private void LastInstrumentRestoreTimer_Tick(object sender, EventArgs e)
        {
            lastInstrumentRestoreAttempts++;

            bool restored = TryLoadLastInstrument();
            if (restored)
                lastInstrumentRestoreStableTicks++;
            else
                lastInstrumentRestoreStableTicks = 0;

            // Considera restaurado somente depois de 8 ciclos consecutivos (aprox. 2 s).
            // Isso atravessa o periodo em que o InstrumentSelector costuma se reinicializar.
            if (lastInstrumentRestoreStableTicks >= 8 || lastInstrumentRestoreAttempts >= 40)
            {
                lastInstrumentRestoreTimer.Stop();
                lastInstrumentRestoreTimer.Tick -= LastInstrumentRestoreTimer_Tick;
                lastInstrumentRestoreTimer = null;
            }
        }

        private bool TryLoadLastInstrument()
        {
            if (instrumentSelector == null)
                return false;

            try
            {
                string instrumentName = ReadLastInstrumentName();
                if (string.IsNullOrWhiteSpace(instrumentName))
                    return true;

                // Se o seletor ja esta exatamente no ativo salvo, terminou.
                Instrument current = instrumentSelector.Instrument;
                if (current != null && string.Equals(current.FullName, instrumentName, StringComparison.OrdinalIgnoreCase))
                    return true;

                Instrument savedInstrument = Instrument.GetInstrument(instrumentName);
                if (savedInstrument == null)
                    return false;

                instrumentSelector.Instrument = savedInstrument;

                // Confirma no proprio seletor. Se o Ninja ainda nao aceitou, o timer tenta novamente.
                current = instrumentSelector.Instrument;
                return current != null && string.Equals(current.FullName, savedInstrument.FullName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void ChangeInstrument(Instrument newInstrument)
        {
            UnsubscribeMarketData();

            currentInstrument = newInstrument;
            ladderOffsetTicks = 0;
            ladderManualNavigation = false;
            ladderDisplayCenter = double.NaN;
            lastPrice = 0;
            bidPrice = 0;
            askPrice = 0;

            buyFlow.Clear();
            sellFlow.Clear();
            timedFlowTrades.Clear();
            lastAggressorSide = 0;
            flowTrades = 0;
            dailyVolume.Clear();
            historicalProfileLoading = false;
            liveVolumeDuringHistoryLoad.Clear();
            lastVolumeBridgeVersion = -1;
            maxDailyVolume = 0;
            profilePoc = double.NaN;
            profileVah = double.NaN;
            profileVal = double.NaN;
            previewOrderPrice = double.NaN;
            previewOrderQuantity = 0;
            previewOrderSide = string.Empty;
            previewOrderType = string.Empty;
            ClearQuotesAndLadder();

            if (currentInstrument == null)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • SELECIONE UM ATIVO";
                return;
            }

            connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • CONECTANDO MARKET DATA";

            marketData = new MarketData(currentInstrument);
            marketData.Update += OnMarketData;

            if (marketData.Bid != null)
                bidPrice = marketData.Bid.Price;

            if (marketData.Ask != null)
                askPrice = marketData.Ask.Price;

            if (marketData.Last != null)
                lastPrice = marketData.Last.Price;

            LoadHistoricalProfile();
            LoadHistoricalFlowForPeriod();
            UpdateDisplay();
        }

        // V0.9.9.22: carrega o historico de negocios de 1 tick do dia.
        // Isso permite iniciar POC/VAH/VAL sem esperar a janela acumular do zero.
        // V0.9.9.94 - pré-carrega o fluxo do período escolhido com ticks históricos.
        // A classificação usa o movimento entre ticks (uptick/downtick) como aproximação
        // histórica do agressor, pois o BarsRequest de 1 tick não traz o Bid/Ask histórico.
        private void LoadHistoricalFlowForPeriod()
        {
            if (currentInstrument == null)
                return;

            if (historicalFlowRequest != null)
            {
                historicalFlowRequest.Dispose();
                historicalFlowRequest = null;
            }

            DateTime now = DateTime.Now;
            DateTime from = now.AddMinutes(-selectedPeriodMinutes);

            historicalFlowRequest = new BarsRequest(currentInstrument, from, now);
            historicalFlowRequest.BarsPeriod = new BarsPeriod
            {
                BarsPeriodType = BarsPeriodType.Tick,
                Value = 1
            };

            BarsRequest request = historicalFlowRequest;
            request.Request(new Action<BarsRequest, ErrorCode, string>(
                (barsRequest, errorCode, errorMessage) =>
                {
                    if (errorCode != ErrorCode.NoError)
                        return;

                    List<TimedFlowTrade> historyTrades = new List<TimedFlowTrade>();
                    double previousPrice = 0;
                    int previousSide = 0;

                    for (int i = 0; i < barsRequest.Bars.Count; i++)
                    {
                        DateTime t = barsRequest.Bars.GetTime(i);
                        double p = currentInstrument.MasterInstrument.RoundToTickSize(
                            barsRequest.Bars.GetClose(i));
                        long v = barsRequest.Bars.GetVolume(i);

                        if (p <= 0 || v <= 0)
                            continue;

                        int side;
                        if (previousPrice <= 0)
                            side = previousSide;
                        else if (p > previousPrice)
                            side = 1;
                        else if (p < previousPrice)
                            side = -1;
                        else
                            side = previousSide;

                        if (side == 0)
                            side = 1;

                        historyTrades.Add(new TimedFlowTrade
                        {
                            Time = t,
                            Price = p,
                            Volume = v,
                            Side = side
                        });

                        previousPrice = p;
                        previousSide = side;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (historicalFlowRequest != request || currentInstrument == null)
                            return;

                        DateTime cutoff = DateTime.Now.AddMinutes(-selectedPeriodMinutes);

                        // Mantém os negócios ao vivo que chegaram depois do fim do request.
                        List<TimedFlowTrade> liveAfterRequest = new List<TimedFlowTrade>();
                        foreach (TimedFlowTrade liveTrade in timedFlowTrades)
                            if (liveTrade.Time > now)
                                liveAfterRequest.Add(liveTrade);

                        timedFlowTrades.Clear();
                        foreach (TimedFlowTrade trade in historyTrades)
                            if (trade.Time >= cutoff)
                                timedFlowTrades.Enqueue(trade);
                        foreach (TimedFlowTrade trade in liveAfterRequest)
                            timedFlowTrades.Enqueue(trade);

                        RebuildFlowFromTimedTrades();
                        UpdateDisplay();
                    }));
                }));
        }

        private void LoadHistoricalProfile()
        {
            if (currentInstrument == null)
                return;

            if (historicalProfileRequest != null)
            {
                historicalProfileRequest.Dispose();
                historicalProfileRequest = null;
            }

            // V0.9.9.22: perfil da sessão operacional 19:00 -> agora.
            // Antes das 19:00, a sessão começou às 19:00 do dia anterior.
            // A partir das 19:00, começa às 19:00 do próprio dia.
            DateTime now = DateTime.Now;
            DateTime sessionStart = now.TimeOfDay >= new TimeSpan(19, 0, 0)
                ? now.Date.AddHours(19)
                : now.Date.AddDays(-1).AddHours(19);

            profileRequestedFrom = sessionStart;
            profileRequestedTo = now;

            historicalProfileLoading = true;
            liveVolumeDuringHistoryLoad.Clear();

            historicalProfileRequest = new BarsRequest(currentInstrument, sessionStart, now);
            historicalProfileRequest.BarsPeriod = new BarsPeriod
            {
                BarsPeriodType = BarsPeriodType.Tick,
                Value = 1
            };

            BarsRequest request = historicalProfileRequest;

            request.Request(new Action<BarsRequest, ErrorCode, string>(
                (barsRequest, errorCode, errorMessage) =>
                {
                    if (errorCode != ErrorCode.NoError)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            historicalProfileLoading = false;
                            liveVolumeDuringHistoryLoad.Clear();
                        }));
                        return;
                    }

                    Dictionary<double, long> history = new Dictionary<double, long>();
                    DateTime firstBar = DateTime.MinValue;
                    DateTime lastBar = DateTime.MinValue;
                    double minHistoryPrice = double.MaxValue;
                    double maxHistoryPrice = double.MinValue;
                    long historyTotalVolume = 0;

                    for (int i = 0; i < barsRequest.Bars.Count; i++)
                    {
                        DateTime barTime = barsRequest.Bars.GetTime(i);
                        if (firstBar == DateTime.MinValue || barTime < firstBar)
                            firstBar = barTime;
                        if (lastBar == DateTime.MinValue || barTime > lastBar)
                            lastBar = barTime;

                        double p = currentInstrument.MasterInstrument.RoundToTickSize(
                            barsRequest.Bars.GetClose(i));
                        long v = barsRequest.Bars.GetVolume(i);

                        if (p <= 0 || v <= 0)
                            continue;

                        if (p < minHistoryPrice) minHistoryPrice = p;
                        if (p > maxHistoryPrice) maxHistoryPrice = p;
                        historyTotalVolume += v;

                        long existing;
                        history.TryGetValue(p, out existing);
                        history[p] = existing + v;
                    }

                    int historyBarsCount = barsRequest.Bars.Count;
                    int historyLevelsCount = history.Count;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (historicalProfileRequest != request || currentInstrument == null)
                            return;

                        dailyVolume.Clear();
                        foreach (KeyValuePair<double, long> kv in history)
                            dailyVolume[kv.Key] = kv.Value;

                        // Reaplica somente os negócios que chegaram ao vivo
                        // durante o carregamento do BarsRequest.
                        foreach (KeyValuePair<double, long> kv in liveVolumeDuringHistoryLoad)
                        {
                            long existing;
                            dailyVolume.TryGetValue(kv.Key, out existing);
                            dailyVolume[kv.Key] = existing + kv.Value;
                        }

                        historicalProfileLoading = false;
                        liveVolumeDuringHistoryLoad.Clear();

                        profileFirstBarTime = firstBar;
                        profileLastBarTime = lastBar;
                        profileHistoryMinPrice = minHistoryPrice == double.MaxValue ? double.NaN : minHistoryPrice;
                        profileHistoryMaxPrice = maxHistoryPrice == double.MinValue ? double.NaN : maxHistoryPrice;
                        profileHistoryTotalVolume = historyTotalVolume;
                        profileHistoryLevels = historyLevelsCount;
                        profileHistoryBars = historyBarsCount;

                        lastVolumeBridgeVersion = -2;
                        RecalculateDailyProfile();
                        UpdateDisplay();
                    }));
                }));
        }

        private void OnMarketData(object sender, MarketDataEventArgs e)
        {
            // GDOM108 PERFORMANCE: do not enqueue a Dispatcher callback for every tick.
            // Capture the event and let the 100 ms UI timer process the batch.
            double eventBid = bidPrice;
            double eventAsk = askPrice;

            if (marketData != null)
            {
                if (marketData.Bid != null && marketData.Bid.Price > 0)
                    eventBid = marketData.Bid.Price;
                if (marketData.Ask != null && marketData.Ask.Price > 0)
                    eventAsk = marketData.Ask.Price;
            }

            PendingMarketEvent pending = new PendingMarketEvent
            {
                Type = e.MarketDataType,
                Price = e.Price,
                Volume = e.Volume,
                Bid = eventBid,
                Ask = eventAsk
            };

            lock (pendingMarketSync)
                pendingMarketEvents.Enqueue(pending);
        }

        private void DrainPendingMarketEvents()
        {
            PendingMarketEvent[] batch;
            lock (pendingMarketSync)
            {
                if (pendingMarketEvents.Count == 0)
                    return;
                batch = pendingMarketEvents.ToArray();
                pendingMarketEvents.Clear();
            }

            for (int i = 0; i < batch.Length; i++)
            {
                PendingMarketEvent e = batch[i];
                if (e.Type == MarketDataType.Bid)
                    bidPrice = e.Price;
                else if (e.Type == MarketDataType.Ask)
                    askPrice = e.Price;
                else if (e.Type == MarketDataType.Last)
                {
                    lastPrice = e.Price;
                    if (e.Bid > 0) bidPrice = e.Bid;
                    if (e.Ask > 0) askPrice = e.Ask;
                    AccumulateTrade(e.Price, e.Volume, e.Ask, e.Bid);
                }
            }
        }

        private void AccumulateTrade(double price, long volume, double ask, double bid)
        {
            if (currentInstrument == null || price <= 0 || volume <= 0)
                return;

            double tickSize = currentInstrument.MasterInstrument.TickSize;
            double levelPrice = currentInstrument.MasterInstrument.RoundToTickSize(price);
            int side = 0;

            // Mesma ideia validada no teste Flow:
            // negócio no/above ASK = comprador agressor;
            // negócio no/below BID = vendedor agressor;
            // entre os dois = mantém o último lado conhecido.
            if (ask > 0 && price >= ask - tickSize * 0.25)
                side = 1;
            else if (bid > 0 && price <= bid + tickSize * 0.25)
                side = -1;
            else
                side = lastAggressorSide;

            if (side == 0)
                return;

            Dictionary<double, long> target = side > 0 ? buyFlow : sellFlow;

            long current;
            target.TryGetValue(levelPrice, out current);
            target[levelPrice] = current + volume;

            // V0.9.9.18: perfil local ao vivo por preço.
            long localDaily;
            dailyVolume.TryGetValue(levelPrice, out localDaily);
            dailyVolume[levelPrice] = localDaily + volume;

            // Enquanto o histórico está sendo carregado, guarda também estes
            // negócios num buffer. Depois o histórico substitui a base e o
            // buffer é reaplicado, evitando perder negócios ocorridos durante
            // o request.
            if (historicalProfileLoading)
            {
                long buffered;
                liveVolumeDuringHistoryLoad.TryGetValue(levelPrice, out buffered);
                liveVolumeDuringHistoryLoad[levelPrice] = buffered + volume;
            }

            // V0.9.9.40: não recalcular o perfil inteiro a cada negócio.
            // O timer de 100 ms faz o recálculo consolidado.
            lastAggressorSide = side;

            timedFlowTrades.Enqueue(new TimedFlowTrade
            {
                Time = DateTime.Now,
                Price = levelPrice,
                Volume = volume,
                Side = side
            });

            TrimTimedFlowTrades();
            flowTrades = timedFlowTrades.Count;
        }

        private void TrimTimedFlowTrades()
        {
            DateTime cutoff = DateTime.Now.AddMinutes(-selectedPeriodMinutes);

            while (timedFlowTrades.Count > 0 && timedFlowTrades.Peek().Time < cutoff)
            {
                TimedFlowTrade oldTrade = timedFlowTrades.Dequeue();
                Dictionary<double, long> target = oldTrade.Side > 0 ? buyFlow : sellFlow;

                long current;
                if (target.TryGetValue(oldTrade.Price, out current))
                {
                    long remaining = current - oldTrade.Volume;
                    if (remaining > 0)
                        target[oldTrade.Price] = remaining;
                    else
                        target.Remove(oldTrade.Price);
                }
            }

            flowTrades = timedFlowTrades.Count;
        }

        private void RebuildFlowFromTimedTrades()
        {
            buyFlow.Clear();
            sellFlow.Clear();

            DateTime cutoff = DateTime.Now.AddMinutes(-selectedPeriodMinutes);
            Queue<TimedFlowTrade> kept = new Queue<TimedFlowTrade>();

            while (timedFlowTrades.Count > 0)
            {
                TimedFlowTrade trade = timedFlowTrades.Dequeue();
                if (trade.Time < cutoff)
                    continue;

                kept.Enqueue(trade);

                Dictionary<double, long> target = trade.Side > 0 ? buyFlow : sellFlow;
                long current;
                target.TryGetValue(trade.Price, out current);
                target[trade.Price] = current + trade.Volume;
            }

            while (kept.Count > 0)
                timedFlowTrades.Enqueue(kept.Dequeue());

            flowTrades = timedFlowTrades.Count;
        }

        private void RefreshDailyProfile()
        {
            if (currentInstrument == null)
                return;

            // GDOM126 STANDALONE:
            // O perfil diario e mantido pelo proprio GuardianDOM (historico + negocios ao vivo).
            // Nao depende do GuardianVolumeBridge/Guardian VolumePro, permitindo exportar
            // o GuardianDOM como pacote NinjaScript independente.
            if (dailyVolume.Count > 0)
                RecalculateDailyProfile();
        }

        private void RecalculateDailyProfile()
        {
            maxDailyVolume = 0;
            profilePoc = double.NaN;
            profileVah = double.NaN;
            profileVal = double.NaN;

            if (dailyVolume.Count == 0 || currentInstrument == null)
                return;

            double tick = currentInstrument.MasterInstrument.TickSize;
            if (tick <= 0)
                return;

            // V0.9.9.22:
            // Normaliza o perfil para uma escada CONTÍNUA de ticks.
            // A versão anterior usava apenas preços existentes no Dictionary.
            // Quando havia níveis ausentes, VAH/VAL podiam saltar por grandes
            // regiões sem volume e produzir uma área artificialmente ampla.
            double minPrice = double.MaxValue;
            double maxPrice = double.MinValue;
            long total = 0;

            foreach (KeyValuePair<double, long> kv in dailyVolume)
            {
                if (kv.Value <= 0)
                    continue;

                double p = currentInstrument.MasterInstrument.RoundToTickSize(kv.Key);
                if (p < minPrice) minPrice = p;
                if (p > maxPrice) maxPrice = p;
                total += kv.Value;
            }

            if (total <= 0 || minPrice == double.MaxValue || maxPrice == double.MinValue)
                return;

            int levels = (int)Math.Round((maxPrice - minPrice) / tick) + 1;
            if (levels <= 0 || levels > 200000)
                return;

            List<double> prices = new List<double>(levels);
            List<long> volumes = new List<long>(levels);

            int pocIndex = -1;
            long pocVolume = -1;

            for (int i = 0; i < levels; i++)
            {
                double p = currentInstrument.MasterInstrument.RoundToTickSize(minPrice + i * tick);
                long v;
                if (!dailyVolume.TryGetValue(p, out v))
                    v = 0;

                prices.Add(p);
                volumes.Add(v);

                // Em empate de volume, escolhe o nível mais próximo do LAST.
                if (v > pocVolume ||
                    (v == pocVolume && pocIndex >= 0 &&
                     Math.Abs(p - lastPrice) < Math.Abs(prices[pocIndex] - lastPrice)))
                {
                    pocVolume = v;
                    pocIndex = i;
                }
            }

            if (pocIndex < 0 || pocVolume <= 0)
                return;

            profilePoc = prices[pocIndex];
            maxDailyVolume = pocVolume;

            long target = (long)Math.Ceiling(total * 0.70);
            long accumulated = volumes[pocIndex];
            int low = pocIndex;
            int high = pocIndex;

            // Expansão contígua a partir do POC.
            // Em cada passo compara o próximo tick acima/abaixo e inclui
            // o lado de maior volume. Em empate, inclui os dois lados.
            while (accumulated < target && (low > 0 || high < prices.Count - 1))
            {
                long below = low > 0 ? volumes[low - 1] : -1;
                long above = high < prices.Count - 1 ? volumes[high + 1] : -1;

                if (above < 0 && below < 0)
                    break;

                if (above > below)
                {
                    high++;
                    accumulated += volumes[high];
                }
                else if (below > above)
                {
                    low--;
                    accumulated += volumes[low];
                }
                else
                {
                    if (low > 0)
                    {
                        low--;
                        accumulated += volumes[low];
                    }

                    if (accumulated < target && high < prices.Count - 1)
                    {
                        high++;
                        accumulated += volumes[high];
                    }
                }
            }

            profileVal = prices[low];
            profileVah = prices[high];
        }

        private bool SameProfilePrice(double a, double b)
        {
            if (currentInstrument == null || double.IsNaN(a) || double.IsNaN(b))
                return false;

            return Math.Abs(a - b) <
                currentInstrument.MasterInstrument.TickSize * 0.5;
        }

        private void MonitorBreakevenTriggers()
        {
            if (!beMonitorActive || currentInstrument == null ||
                double.IsNaN(beEntryFillPrice) || beEntryDirection == 0)
                return;

            double marketPrice = lastPrice;
            if (marketPrice <= 0)
                return;

            double tick = currentInstrument.MasterInstrument.TickSize;
            if (tick <= 0)
                return;

            double favorableTicks = ((marketPrice - beEntryFillPrice) / tick) * beEntryDirection;

            if (!be20Detected && favorableTicks >= 20.0)
            {
                be20Detected = true;

                // BE1 SOMENTE: +20t de avanço => stop fica a -20t da entrada.
                // Não existe BE2 nesta versão.
                if (!be1ChangeSent && guardianStopOrder != null && guardianOrderAccount != null)
                {
                    OrderState stopState = guardianStopOrder.OrderState;
                    bool stopWorking =
                        stopState == OrderState.Accepted ||
                        stopState == OrderState.Working ||
                        stopState == OrderState.TriggerPending;

                    if (stopWorking)
                    {
                        double newStop = beEntryDirection > 0
                            ? beEntryFillPrice - 20.0 * tick
                            : beEntryFillPrice + 20.0 * tick;

                        newStop = guardianStopOrder.Instrument.MasterInstrument.RoundToTickSize(newStop);

                        // Não piora um stop que já esteja manualmente mais protegido.
                        bool improves =
                            (beEntryDirection > 0 && (guardianStopOrder.StopPrice <= 0 || newStop > guardianStopOrder.StopPrice)) ||
                            (beEntryDirection < 0 && (guardianStopOrder.StopPrice <= 0 || newStop < guardianStopOrder.StopPrice));

                        if (improves)
                        {
                            try
                            {
                                // Marca antes da chamada para impedir repetição a cada refresh.
                                be1ChangeSent = true;
                                guardianStopOrder.StopPriceChanged = newStop;
                                guardianOrderAccount.Change(new[] { guardianStopOrder });

                                if (orderStateStatus != null)
                                {
                                    orderStateStatus.Text = "BE1 SOLICITADO • +20t → STOP -20t";
                                    orderStateStatus.Foreground = Brushes.Gold;
                                }
                            }
                            catch (Exception ex)
                            {
                                be1ChangeSent = false;
                                if (orderStateStatus != null)
                                {
                                    orderStateStatus.Text = "ERRO BE1 • STOP NÃO ALTERADO";
                                    orderStateStatus.Foreground = Brushes.OrangeRed;
                                }
                                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • ERRO BE1: " + ex.Message;
                            }
                        }
                        else
                        {
                            be1ChangeSent = true;
                            if (orderStateStatus != null)
                            {
                                orderStateStatus.Text = "BE1 • STOP JÁ ESTÁ MAIS PROTEGIDO";
                                orderStateStatus.Foreground = Brushes.LimeGreen;
                            }
                        }
                    }
                }
            }

            // Nesta versão +40t é apenas informativo; NÃO move o stop.
            if (!be40Detected && favorableTicks >= 40.0)
            {
                be40Detected = true;
                if (orderStateStatus != null && !be1ChangeSent)
                {
                    orderStateStatus.Text = "BE MONITOR • +40t DETECTADO";
                    orderStateStatus.Foreground = Brushes.LimeGreen;
                }
            }
        }

        // V0.9.9.67 - PnL não realizado da posição do ativo selecionado.
        private void UpdateMarketPnl()
        {
            if (marketPnlValue == null)
                return;

            double pnl = 0.0;
            double points = 0.0;
            double ticks = 0.0;
            try
            {
                Account account = accountSelector == null ? null : accountSelector.SelectedAccount;
                if (account != null && currentInstrument != null && lastPrice > 0)
                {
                    foreach (Position position in account.Positions)
                    {
                        if (position == null || position.Instrument == null)
                            continue;

                        if (string.Equals(position.Instrument.FullName, currentInstrument.FullName, StringComparison.OrdinalIgnoreCase))
                        {
                            pnl = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, lastPrice);

                            // Igual à leitura de deslocamento de preço do Ninja: pontos/ticks
                            // representam o movimento desde o preço médio, sem multiplicar pela QTD.
                            if (position.MarketPosition != MarketPosition.Flat && position.AveragePrice > 0)
                            {
                                double direction = position.MarketPosition == MarketPosition.Long ? 1.0 : -1.0;
                                points = (lastPrice - position.AveragePrice) * direction;
                                double tickSize = currentInstrument.MasterInstrument.TickSize;
                                if (tickSize > 0)
                                    ticks = points / tickSize;
                            }
                            break;
                        }
                    }
                }
            }
            catch
            {
                pnl = 0.0;
                points = 0.0;
                ticks = 0.0;
            }

            if (marketPnlDisplayMode == 1)
                marketPnlValue.Text = "PnL  " + ticks.ToString("0.##") + " ticks";
            else if (marketPnlDisplayMode == 2)
                marketPnlValue.Text = "PnL  " + points.ToString("0.00") + " pts";
            else
                marketPnlValue.Text = "PnL  " + pnl.ToString("C2");

            // A cor acompanha ganho/perda em qualquer modo de exibição.
            double signValue = marketPnlDisplayMode == 0 ? pnl : points;
            marketPnlValue.Foreground = signValue > 0 ? Brushes.LimeGreen : (signValue < 0 ? Brushes.Red : Brushes.WhiteSmoke);
        }

        // GDOM125 - leva diretamente a ladder ao POC/VAH/VAL sem alterar
        // os dados do perfil ou o comportamento do botão CENTRALIZAR.
        private void NavigateToProfileLevel(double targetPrice)
        {
            if (currentInstrument == null || double.IsNaN(targetPrice) || targetPrice <= 0)
                return;

            double anchor;
            if (bidPrice > 0 && askPrice > 0)
                anchor = (bidPrice + askPrice) / 2.0;
            else if (lastPrice > 0)
                anchor = lastPrice;
            else
                anchor = bidPrice > 0 ? bidPrice : askPrice;

            double tickSize = currentInstrument.MasterInstrument.TickSize;
            if (anchor <= 0 || tickSize <= 0)
                return;

            double marketCenter = currentInstrument.MasterInstrument.RoundToTickSize(anchor);
            ladderOffsetTicks = (int)Math.Round((targetPrice - marketCenter) / tickSize);
            ladderManualNavigation = true;
            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            MonitorBreakevenTriggers();
            if (currentInstrument == null)
                return;

            bidValue.Text = FormatPrice(bidPrice);
            askValue.Text = FormatPrice(askPrice);
            lastValue.Text = FormatPrice(lastPrice);
            UpdateMarketPnl();

            if (profileStatus != null)
            {
                string pocText = double.IsNaN(profilePoc) ? "--" : FormatPrice(profilePoc);
                string vahText = double.IsNaN(profileVah) ? "--" : FormatPrice(profileVah);
                string valText = double.IsNaN(profileVal) ? "--" : FormatPrice(profileVal);

                profileStatus.Text =
                    "POC: " + pocText +
                    "   •   VAH: " + vahText +
                    "   •   VAL: " + valText;
            }

            // V0.9.9.3 ATM: keep the ladder centered on the live inside market.
            // Prefer the midpoint of Bid/Ask; fall back to Last when needed.
            double anchor;

            if (bidPrice > 0 && askPrice > 0)
                anchor = (bidPrice + askPrice) / 2.0;
            else if (lastPrice > 0)
                anchor = lastPrice;
            else
                anchor = bidPrice > 0 ? bidPrice : askPrice;

            if (anchor <= 0)
            {
                connectionStatus.Text = "V0.9.9.89 PERÍODO GRÁFICO • AGUARDANDO COTAÇÃO • ENVIO SOMENTE POR BOTÃO";
                return;
            }

            connectionStatus.Text = "V0.9.9.89 • PERÍODO: " + selectedPeriodMinutes.ToString() + "m • NEGÓCIOS: " + flowTrades.ToString()
                + " • PERFIL: " + (dailyVolume.Count > 0
                    ? (lastVolumeBridgeVersion >= 0 ? "VOLUMEPRO OK"
                        : (lastVolumeBridgeVersion == -2 ? "SESSÃO 19H + AO VIVO" : "LOCAL AO VIVO"))
                    : "CARREGANDO HISTÓRICO")
                + (ladderManualNavigation ? " • LADDER: MANUAL " + (ladderOffsetTicks >= 0 ? "+" : "") + ladderOffsetTicks.ToString() + "t" : " • LADDER: AUTO")
                + " • ENVIO SOMENTE POR BOTÃO";

            double tickSize = currentInstrument.MasterInstrument.TickSize;
            double marketCenter = currentInstrument.MasterInstrument.RoundToTickSize(anchor);
            double center;

            if (ladderManualNavigation)
            {
                // Manual continua exatamente como antes: deslocamento relativo ao mercado.
                center = currentInstrument.MasterInstrument.RoundToTickSize(
                    marketCenter + ladderOffsetTicks * tickSize);
            }
            else
            {
                // V0.9.9.43 SMART AUTO-CENTER:
                // mantém a ladder parada enquanto o preço permanece na zona segura.
                // Só recentraliza quando faltarem 5 níveis para uma das bordas.
                if (double.IsNaN(ladderDisplayCenter))
                {
                    ladderDisplayCenter = marketCenter;
                }
                else
                {
                    double distanceTicks = Math.Abs((marketCenter - ladderDisplayCenter) / tickSize);
                    int triggerTicks = CenterRow - AutoCenterEdgeRows; // GDOM107: centro acompanha a ladder compacta
                    if (distanceTicks >= triggerTicks)
                        ladderDisplayCenter = marketCenter;
                }

                center = currentInstrument.MasterInstrument.RoundToTickSize(ladderDisplayCenter);
            }

            long maxBuyVisible = 0;
            long maxSellVisible = 0;
            long maxDailyVisible = 0;
            long minBuyVisible = long.MaxValue;
            long minSellVisible = long.MaxValue;
            long minDailyVisible = long.MaxValue;
            long minPositiveDeltaVisible = long.MaxValue;
            long maxPositiveDeltaVisible = 0;
            long minNegativeAbsDeltaVisible = long.MaxValue;
            long maxNegativeAbsDeltaVisible = 0;
            long maxAbsDeltaVisible = 1;

            // V0.9.9.25 - saldo acumulado da agressão da sessão/local atual.
            long totalBuyAggression = 0;
            long totalSellAggression = 0;

            foreach (KeyValuePair<double, long> kv in buyFlow)
                totalBuyAggression += kv.Value;

            foreach (KeyValuePair<double, long> kv in sellFlow)
                totalSellAggression += kv.Value;

            long aggressionBalance = totalBuyAggression - totalSellAggression;

            if (aggressionBalanceStatus != null)
            {
                aggressionBalanceStatus.Text =
                    "SALDO AGRESSÃO: " +
                    (aggressionBalance > 0 ? "+" : "") +
                    aggressionBalance.ToString() +
                    "   •   C: " + totalBuyAggression.ToString() +
                    "   •   V: " + totalSellAggression.ToString();

                aggressionBalanceStatus.Foreground = aggressionBalance > 0
                    ? new SolidColorBrush(Color.FromRgb(90, 220, 120))
                    : (aggressionBalance < 0
                        ? new SolidColorBrush(Color.FromRgb(240, 105, 105))
                        : Brushes.WhiteSmoke);
            }

            for (int scan = 0; scan < LadderRows; scan++)
            {
                double scanPrice = center + (CenterRow - scan) * tickSize;
                scanPrice = currentInstrument.MasterInstrument.RoundToTickSize(scanPrice);

                long bv;
                long sv;
                buyFlow.TryGetValue(scanPrice, out bv);
                sellFlow.TryGetValue(scanPrice, out sv);

                if (bv > maxBuyVisible) maxBuyVisible = bv;
                if (sv > maxSellVisible) maxSellVisible = sv;
                if (bv > 0 && bv < minBuyVisible) minBuyVisible = bv;
                if (sv > 0 && sv < minSellVisible) minSellVisible = sv;

                long dv;
                dailyVolume.TryGetValue(scanPrice, out dv);
                if (dv > maxDailyVisible) maxDailyVisible = dv;
                if (dv > 0 && dv < minDailyVisible) minDailyVisible = dv;

                long scanDelta = bv - sv;
                long scanAbsDelta = Math.Abs(scanDelta);
                if (scanAbsDelta > maxAbsDeltaVisible)
                    maxAbsDeltaVisible = scanAbsDelta;

                if (scanDelta > 0)
                {
                    if (scanDelta > maxPositiveDeltaVisible) maxPositiveDeltaVisible = scanDelta;
                    if (scanDelta < minPositiveDeltaVisible) minPositiveDeltaVisible = scanDelta;
                }
                else if (scanDelta < 0)
                {
                    long negAbs = Math.Abs(scanDelta);
                    if (negAbs > maxNegativeAbsDeltaVisible) maxNegativeAbsDeltaVisible = negAbs;
                    if (negAbs < minNegativeAbsDeltaVisible) minNegativeAbsDeltaVisible = negAbs;
                }
            }

            for (int row = 0; row < LadderRows; row++)
            {
                double levelPrice = center + (CenterRow - row) * tickSize;
                levelPrice = currentInstrument.MasterInstrument.RoundToTickSize(levelPrice);

                string priceLabel = currentInstrument.MasterInstrument.FormatPrice(levelPrice);
                if (SameProfilePrice(levelPrice, profilePoc)) priceLabel += "  POC";
                else if (SameProfilePrice(levelPrice, profileVah)) priceLabel += "  VAH";
                else if (SameProfilePrice(levelPrice, profileVal)) priceLabel += "  VAL";
                priceCells[row].Text = priceLabel;
                priceCells[row].Tag = levelPrice;

                bidCells[row].Text = "";
                askCells[row].Text = "";

                long buyVolume = 0;
                long sellVolume = 0;

                buyFlow.TryGetValue(levelPrice, out buyVolume);
                sellFlow.TryGetValue(levelPrice, out sellVolume);

                if (buyVolume > 0)
                    bidCells[row].Text = buyVolume.ToString();

                if (sellVolume > 0)
                    askCells[row].Text = sellVolume.ToString();

                long delta = buyVolume - sellVolume;
                if (deltaCells != null && deltaCells[row] != null)
                {
                    double deltaRatio = 0.0;
                    if (delta > 0)
                    {
                        long posMin = minPositiveDeltaVisible == long.MaxValue ? 0 : minPositiveDeltaVisible;
                        long posRange = maxPositiveDeltaVisible - posMin;
                        deltaRatio = posRange > 0
                            ? Math.Max(0.0, Math.Min(1.0, (double)(delta - posMin) / posRange))
                            : 1.0;
                    }
                    else if (delta < 0)
                    {
                        long negAbs = Math.Abs(delta);
                        long negMin = minNegativeAbsDeltaVisible == long.MaxValue ? 0 : minNegativeAbsDeltaVisible;
                        long negRange = maxNegativeAbsDeltaVisible - negMin;
                        deltaRatio = negRange > 0
                            ? Math.Max(0.0, Math.Min(1.0, (double)(negAbs - negMin) / negRange))
                            : 1.0;
                    }

                    double halfWidth = deltaBorders[row].ActualWidth * 0.5;
                    double deltaBarWidth = Math.Max(0, halfWidth * Math.Sqrt(deltaRatio));

                    deltaNegativeBars[row].Width = delta < 0 ? deltaBarWidth : 0;
                    deltaPositiveBars[row].Width = delta > 0 ? deltaBarWidth : 0;

                    double deltaHeat = Math.Sqrt(deltaRatio);
                    deltaNegativeBars[row].Opacity = delta < 0 ? 0.30 + (0.70 * deltaHeat) : 0.0;
                    deltaPositiveBars[row].Opacity = delta > 0 ? 0.30 + (0.70 * deltaHeat) : 0.0;

                    deltaNegativeCells[row].Text = delta < 0 ? delta.ToString() : "";
                    deltaPositiveCells[row].Text = delta > 0 ? delta.ToString() : "";

                    // Zero fica junto ao eixo central, no lado direito.
                    if (delta == 0)
                        deltaPositiveCells[row].Text = "0";
                }

                // Largura relativa ao maior volume visível de cada lado.
                double buyRatio = 0.0;
                if (buyVolume > 0)
                {
                    long buyMin = minBuyVisible == long.MaxValue ? 0 : minBuyVisible;
                    long buyRange = maxBuyVisible - buyMin;
                    buyRatio = buyRange > 0
                        ? Math.Max(0.0, Math.Min(1.0, (double)(buyVolume - buyMin) / buyRange))
                        : 1.0;
                }

                double sellRatio = 0.0;
                if (sellVolume > 0)
                {
                    long sellMin = minSellVisible == long.MaxValue ? 0 : minSellVisible;
                    long sellRange = maxSellVisible - sellMin;
                    sellRatio = sellRange > 0
                        ? Math.Max(0.0, Math.Min(1.0, (double)(sellVolume - sellMin) / sellRange))
                        : 1.0;
                }

                double buyWidth = bidBorders[row].ActualWidth * buyRatio;
                double sellWidth = askBorders[row].ActualWidth * sellRatio;

                buyFlowBars[row].Width = Math.Max(0, buyWidth);
                sellFlowBars[row].Width = Math.Max(0, sellWidth);

                // V0.9.9.46 - HEAT INTENSITY.
                // Além da largura proporcional, a intensidade também acompanha
                // a força relativa do nível visível. Valores pequenos ficam suaves;
                // os maiores ficam fortes, como no DOM antigo.
                double buyHeat = Math.Sqrt(buyRatio);
                double sellHeat = Math.Sqrt(sellRatio);
                buyFlowBars[row].Opacity = buyVolume > 0 ? 0.30 + (0.70 * buyHeat) : 0.0;
                sellFlowBars[row].Opacity = sellVolume > 0 ? 0.30 + (0.70 * sellHeat) : 0.0;

                // GDOM110 - TENDÊNCIA NA COLUNA PREÇO.
                // Usa o saldo de agressão do período atual já calculado pelo GuardianDOM:
                // compradores dominando = coluna PREÇO verde;
                // vendedores dominando = coluna PREÇO vermelha;
                // empate/sem fluxo = fundo neutro.
                // A linha LAST continua amarela e tem prioridade visual abaixo.
                Brush priceBackground = aggressionBalance > 0
                    ? new SolidColorBrush(Color.FromRgb(0, 128, 0))
                    : (aggressionBalance < 0
                        ? new SolidColorBrush(Color.FromRgb(139, 0, 0))
                        : LadderBaseBrush);

                long daily = 0;
                dailyVolume.TryGetValue(levelPrice, out daily);
                volumeCells[row].Text = daily > 0 ? daily.ToString() : "";

                double volumeRatio = 0.0;
                if (daily > 0)
                {
                    long dailyMin = minDailyVisible == long.MaxValue ? 0 : minDailyVisible;
                    long dailyRange = maxDailyVisible - dailyMin;
                    volumeRatio = dailyRange > 0
                        ? Math.Max(0.0, Math.Min(1.0, (double)(daily - dailyMin) / dailyRange))
                        : 1.0;
                }

                // Mantém o mesmo princípio visual do Guardian Volume estável:
                // raiz quadrada evita comprimir demais os níveis menores.
                double visualRatio = Math.Sqrt(volumeRatio);
                volumeBars[row].Width = Math.Max(0,
                    volumeBorders[row].ActualWidth * visualRatio);

                Brush normalVolumeColor = new SolidColorBrush(Color.FromRgb(105, 105, 110));
                if (SameProfilePrice(levelPrice, profilePoc))
                {
                    volumeBars[row].Background = new SolidColorBrush(Color.FromRgb(225, 45, 45));
                    volumeBars[row].Opacity = 1.0;
                }
                else if (SameProfilePrice(levelPrice, profileVah) ||
                         SameProfilePrice(levelPrice, profileVal))
                {
                    volumeBars[row].Background = new SolidColorBrush(Color.FromRgb(230, 190, 35));
                    volumeBars[row].Opacity = 1.0;
                }
                else
                {
                    volumeBars[row].Background = normalVolumeColor;
                    volumeBars[row].Opacity = daily > 0 ? 0.14 + (0.86 * visualRatio) : 0.0;
                }

                // GDOM123 - maior volume visível recebe uma borda clara discreta.
                volumeBorders[row].BorderBrush = (daily > 0 && daily == maxDailyVisible)
                    ? Brushes.WhiteSmoke
                    : new SolidColorBrush(Color.FromRgb(70, 70, 73));
                volumeBorders[row].BorderThickness = (daily > 0 && daily == maxDailyVisible)
                    ? new Thickness(1.5)
                    : new Thickness(0, 0, 1, 1);

                // V0.9.9.44 - leitura BID / ASK / LAST dentro da ladder.
                // LAST continua com prioridade máxima (amarelo).
                // BID e ASK recebem apenas um realce discreto nas respectivas colunas,
                // sem criar novos controles e sem alterar o throttle de atualização.
                bool isLastRow = lastPrice > 0 &&
                    Math.Abs(levelPrice - currentInstrument.MasterInstrument.RoundToTickSize(lastPrice))
                        < tickSize * 0.5;
                bool isBidRow = bidPrice > 0 &&
                    Math.Abs(levelPrice - currentInstrument.MasterInstrument.RoundToTickSize(bidPrice))
                        < tickSize * 0.5;
                bool isAskRow = askPrice > 0 &&
                    Math.Abs(levelPrice - currentInstrument.MasterInstrument.RoundToTickSize(askPrice))
                        < tickSize * 0.5;

                if (isLastRow)
                {
                    Brush activeRow = new SolidColorBrush(Color.FromRgb(245, 205, 45));

                    // GDOM122 - destaque do LAST atravessa SOMENTE a linha do preço atual.
                    // Todas as colunas lógicas recebem o mesmo fundo; nas linhas seguintes
                    // os fundos de VOLUME/DELTA são explicitamente restaurados.
                    bidBorders[row].Background = activeRow;
                    priceBorders[row].Background = activeRow;
                    askBorders[row].Background = activeRow;
                    volumeBorders[row].Background = activeRow;
                    deltaBorders[row].Background = activeRow;

                    bidCells[row].Foreground = Brushes.Black;
                    priceCells[row].Foreground = Brushes.Black;
                    askCells[row].Foreground = Brushes.Black;
                    volumeCells[row].Foreground = Brushes.White;

                    priceCells[row].FontWeight = FontWeights.Bold;
                }
                else
                {
                    Brush neutralSide = LadderBaseBrush;
                    Brush bidMarker = new SolidColorBrush(Color.FromRgb(30, 73, 105));
                    Brush askMarker = new SolidColorBrush(Color.FromRgb(105, 45, 45));

                    bidBorders[row].Background = isBidRow ? bidMarker : neutralSide;
                    priceBorders[row].Background = priceBackground;
                    askBorders[row].Background = isAskRow ? askMarker : neutralSide;

                    // GDOM122 - impede que o amarelo do LAST permaneça em células
                    // reutilizadas de VOLUME/DELTA quando o preço se desloca.
                    volumeBorders[row].Background = neutralSide;
                    deltaBorders[row].Background = neutralSide;

                    bidCells[row].Foreground = Brushes.WhiteSmoke;
                    priceCells[row].Foreground = Brushes.WhiteSmoke;
                    askCells[row].Foreground = Brushes.WhiteSmoke;
                    volumeCells[row].Foreground = Brushes.WhiteSmoke;

                    priceCells[row].FontWeight = FontWeights.Normal;
                }

                // V0.9.9.82 - IMBALANCE DIAGONAL 3x por BORDA.
                // Footprint clássico:
                //   COMPRA (ASK) neste preço x VENDA (BID) 1 tick ABAIXO.
                //   VENDA  (BID) neste preço x COMPRA (ASK) 1 tick ACIMA.
                // A borda âmbar não altera o fundo/histograma nem a linha do LAST.
                double lowerPrice = currentInstrument.MasterInstrument.RoundToTickSize(levelPrice - tickSize);
                double upperPrice = currentInstrument.MasterInstrument.RoundToTickSize(levelPrice + tickSize);
                long diagonalSellBelow = 0;
                long diagonalBuyAbove = 0;
                sellFlow.TryGetValue(lowerPrice, out diagonalSellBelow);
                buyFlow.TryGetValue(upperPrice, out diagonalBuyAbove);

                bool buyImbalance = imbalanceEnabled && buyVolume >= ImbalanceMinVolume &&
                    (diagonalSellBelow == 0 || (double)buyVolume >= (double)diagonalSellBelow * ImbalanceRatio);
                bool sellImbalance = imbalanceEnabled && sellVolume >= ImbalanceMinVolume &&
                    (diagonalBuyAbove == 0 || (double)sellVolume >= (double)diagonalBuyAbove * ImbalanceRatio);

                buyImbalanceBorders[row].BorderThickness = buyImbalance ? new Thickness(2) : new Thickness(0);
                sellImbalanceBorders[row].BorderThickness = sellImbalance ? new Thickness(2) : new Thickness(0);

                // Texto volta ao padrão: a indicação do imbalance agora é exclusivamente a borda.
                bidCells[row].FontWeight = FontWeights.Normal;
                askCells[row].FontWeight = FontWeights.Normal;
            }

            UpdateOrderPreviewVisual();
        }

        private string FormatPrice(double price)
        {
            if (currentInstrument == null || price <= 0)
                return "--";

            return currentInstrument.MasterInstrument.FormatPrice(price);
        }

        private void ClearQuotesAndLadder()
        {
            bidValue.Text = "--";
            askValue.Text = "--";
            lastValue.Text = "--";

            if (profileStatus != null)
                profileStatus.Text = "POC: --   •   VAH: --   •   VAL: --";

            for (int i = 0; i < LadderRows; i++)
            {
                bidCells[i].Text = "";
                askCells[i].Text = "";
                priceCells[i].Text = "--";
                priceCells[i].Tag = null;

                if (buyFlowBars != null && buyFlowBars[i] != null)
                    buyFlowBars[i].Width = 0;
                if (sellFlowBars != null && sellFlowBars[i] != null)
                    sellFlowBars[i].Width = 0;
                if (volumeCells != null && volumeCells[i] != null)
                    volumeCells[i].Text = "";
                if (volumeBars != null && volumeBars[i] != null)
                    volumeBars[i].Width = 0;
                if (deltaCells != null && deltaCells[i] != null)
                {
                    deltaCells[i].Text = "";
                    deltaCells[i].Foreground = Brushes.WhiteSmoke;
                    if (deltaNegativeBars != null && deltaNegativeBars[i] != null)
                        deltaNegativeBars[i].Width = 0;
                    if (deltaPositiveBars != null && deltaPositiveBars[i] != null)
                        deltaPositiveBars[i].Width = 0;
                    if (deltaNegativeCells != null && deltaNegativeCells[i] != null)
                        deltaNegativeCells[i].Text = "";
                    if (deltaPositiveCells != null && deltaPositiveCells[i] != null)
                        deltaPositiveCells[i].Text = "";
                }

                bidBorders[i].Background = LadderBaseBrush;
                if (buyImbalanceBorders != null && buyImbalanceBorders[i] != null) buyImbalanceBorders[i].BorderThickness = new Thickness(0);
                if (sellImbalanceBorders != null && sellImbalanceBorders[i] != null) sellImbalanceBorders[i].BorderThickness = new Thickness(0);
                priceBorders[i].Background = new SolidColorBrush(Color.FromRgb(62, 62, 65));
                askBorders[i].Background = LadderBaseBrush;

                bidCells[i].Foreground = Brushes.WhiteSmoke;
                priceCells[i].Foreground = Brushes.WhiteSmoke;
                askCells[i].Foreground = Brushes.WhiteSmoke;
                priceCells[i].FontWeight = FontWeights.Normal;
            }
        }

        private void UnsubscribeMarketData()
        {
            if (marketData != null)
            {
                marketData.Update -= OnMarketData;
                marketData = null;
            }

            if (historicalProfileRequest != null)
            {
                historicalProfileRequest.Dispose();
                historicalProfileRequest = null;
            }

            if (historicalFlowRequest != null)
            {
                historicalFlowRequest.Dispose();
                historicalFlowRequest = null;
            }

            buyFlow.Clear();
            sellFlow.Clear();
            timedFlowTrades.Clear();
            lastAggressorSide = 0;
            flowTrades = 0;
            dailyVolume.Clear();
            lastVolumeBridgeVersion = -1;
            maxDailyVolume = 0;
            profilePoc = double.NaN;
            profileVah = double.NaN;
            profileVal = double.NaN;
        }

        private void GuardianDomWindow_Closed(object sender, EventArgs e)
        {
            Closed -= GuardianDomWindow_Closed;

            try
            {
                if (titleInstrumentLinkPopup != null)
                {
                    titleInstrumentLinkPopup.IsOpen = false;
                    titleInstrumentLinkPopup.Child = null;
                    titleInstrumentLinkPopup = null;
                }
                titleInstrumentLinkButton = null;
            }
            catch { }

            lock (guardianLinkSync)
                guardianLinkWindows.Remove(this);

            if (guardianHwndSource != null)
            {
                guardianHwndSource.RemoveHook(GuardianDomWindow_WndProc);
                guardianHwndSource = null;
            }

            if (depthRefreshTimer != null)
            {
                depthRefreshTimer.Stop();
                depthRefreshTimer.Tick -= OnDepthRefreshTimerTick;

            if (fastPriceRefreshTimer != null)
            {
                fastPriceRefreshTimer.Stop();
                fastPriceRefreshTimer.Tick -= OnFastPriceRefreshTimerTick;
            }
                depthRefreshTimer = null;
            }

            if (instrumentSelector != null && instrumentSelector.Instrument != null)
                SaveLastInstrument(instrumentSelector.Instrument);

            if (instrumentSelector != null)
            {
                if (lastInstrumentRestoreTimer != null)
                {
                    lastInstrumentRestoreTimer.Stop();
                    lastInstrumentRestoreTimer.Tick -= LastInstrumentRestoreTimer_Tick;
                    lastInstrumentRestoreTimer = null;
                }

                instrumentSelector.InstrumentChanged -= OnInstrumentChanged;
            }

            if (atmDiagTimer != null)
            {
                atmDiagTimer.Stop();
                atmDiagTimer.Tick -= AtmDiagTimer_Tick;
                atmDiagTimer = null;
            }

            lastAutoQtyAtm = null;

            UnsubscribeMarketData();

            if (instrumentSelector != null)
                instrumentSelector.Cleanup();

            if (sendPreviewButton != null)
                sendPreviewButton.Click -= SendPreviewButton_Click;

            if (cancelOrderButton != null)
                cancelOrderButton.Click -= CancelOrderButton_Click;

            if (guardianOrderAccount != null)
            {
                guardianOrderAccount.OrderUpdate -= GuardianOrderAccount_OrderUpdate;
                guardianOrderAccount = null;
            }

            if (atmStrategySelector != null)
                atmStrategySelector.Cleanup();

            if (accountSelector != null)
                accountSelector.Cleanup();
        }

        // GDOM121B - compacta somente controles EXTERNOS a ladder.
        // Nao toca em header/ladder ColumnDefinitions, ordem, visibilidade ou cores.
        private void ApplyCompactOuterLayout()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            bool compact = w < 430;
            bool veryCompact = w < 330;

            double selectorFont = veryCompact ? 9.0 : (compact ? 10.0 : 12.0);
            double buttonFont = veryCompact ? 10.0 : (compact ? 11.0 : 12.0);
            double statusFont = veryCompact ? 9.0 : (compact ? 10.0 : 11.0);

            if (instrumentSelector != null) instrumentSelector.FontSize = selectorFont;
            if (accountSelector != null) accountSelector.FontSize = selectorFont;
            if (quantitySelector != null) quantitySelector.FontSize = selectorFont;
            if (periodSelector != null) periodSelector.FontSize = selectorFont;
            if (atmStrategySelector != null) atmStrategySelector.FontSize = selectorFont;

            if (marketBuyButton != null) marketBuyButton.FontSize = buttonFont;
            if (marketSellButton != null) marketSellButton.FontSize = buttonFont;
            if (flattenButton != null) flattenButton.FontSize = buttonFont;
            if (sendPreviewButton != null) sendPreviewButton.FontSize = buttonFont;
            if (cancelOrderButton != null) cancelOrderButton.FontSize = buttonFont;
            if (marketPnlValue != null) marketPnlValue.FontSize = buttonFont;

            if (connectionStatus != null) connectionStatus.FontSize = statusFont;
            if (profileStatus != null) profileStatus.FontSize = statusFont;
            if (aggressionBalanceStatus != null) aggressionBalanceStatus.FontSize = statusFont;
            if (orderStateStatus != null) orderStateStatus.FontSize = statusFont;
        }

        private StackPanel CreateSelectorPanel(string label)
        {
            StackPanel panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.LightGray,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 0, 0)
            });

            return panel;
        }

        private TextBlock AddQuoteBox(Grid grid, string label, string value, int column, Brush background)
        {
            Border border = new Border
            {
                Background = background,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 74)),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(2, 1, 2, 1)
            };

            StackPanel panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.LightGray,
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            TextBlock valueBlock = new TextBlock
            {
                Text = value,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            panel.Children.Add(valueBlock);
            border.Child = panel;

            Grid.SetColumn(border, column);
            grid.Children.Add(border);

            return valueBlock;
        }

        private Border AddHeaderCell(Grid grid, string text, int column)
        {
            Border border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 74)),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(4)
            };

            border.Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(border, column);
            grid.Children.Add(border);
            return border;
        }

        private TextBlock AddLadderCell(Grid grid, string text, int row, int column, Brush background, out Border border)
        {
            border = new Border
            {
                Background = background,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 61)),
                // GDOM113 - separação horizontal fixa e mais contrastante.
                // Evita o efeito de linhas "sumindo" causado pela borda de 0,5 px.
                BorderThickness = new Thickness(0, 0, 1, 1)
            };

            TextBlock block = new TextBlock
            {
                Text = text,
                Foreground = Brushes.WhiteSmoke,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            border.Child = block;

            Grid.SetRow(border, row);
            Grid.SetColumn(border, column);
            grid.Children.Add(border);

            return block;
        }
    }
}
