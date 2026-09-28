#region Using declarations
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.SuperDomColumns;
#endregion

namespace NinjaTrader.NinjaScript.AddOns.GuardianDOM
{

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

    public class GuardianDomWindow : NTWindow
    {
        private InstrumentSelector instrumentSelector;
        private AccountSelector accountSelector;
        private QuantityUpDown quantitySelector;
        private NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector atmStrategySelector;
        private System.Windows.Threading.DispatcherTimer atmDiagTimer;
        private NinjaTrader.NinjaScript.AtmStrategy lastAutoQtyAtm;

        private Instrument currentInstrument;
        private MarketData marketData;
        private BarsRequest historicalProfileRequest;
        private DispatcherTimer depthRefreshTimer;

        // Fluxo negociado por preço. Não usa SuperDom.Rows nem MarketDepth.
        // buyFlow = negócios classificados no ASK (agressão compradora).
        // sellFlow = negócios classificados no BID (agressão vendedora).
        private readonly Dictionary<double, long> buyFlow = new Dictionary<double, long>();
        private readonly Dictionary<double, long> sellFlow = new Dictionary<double, long>();
        private int lastAggressorSide; // +1 compra, -1 venda, 0 desconhecido
        private long flowTrades;

        // Perfil diário lido do GuardianVolumeBridge já alimentado pelo
        // Guardian VolumePro V0.10.3. O GuardianDOM NÃO cria outro BarsRequest.
        private readonly Dictionary<double, long> dailyVolume = new Dictionary<double, long>();
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
        private Order guardianSubmittedOrder;
        private Account guardianOrderAccount;
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

        private const int LadderRows = 21;
        private const int CenterRow = 10;

        // Navegação manual da ladder. Zero = acompanha o mercado.
        private int ladderOffsetTicks = 0;
        private bool ladderManualNavigation = false;

        // V0.9.9.43 - centro visual persistente da ladder.
        // Em AUTO, só é deslocado quando o mercado chega perto das bordas.
        private double ladderDisplayCenter = double.NaN;
        private const int AutoCenterEdgeRows = 5;

        public GuardianDomWindow()
        {
            // Estrutura da janela baseada diretamente na GuardianWindow original.
            Caption = "Guardian DOM";
            Width = 555;
            Height = 760;
            MinWidth = 480;
            MinHeight = 560;

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SizeToContent = SizeToContent.Manual;
            Background = new SolidColorBrush(Color.FromRgb(20, 25, 31));

            Content = BuildInterface();

            depthRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            depthRefreshTimer.Tick += OnDepthRefreshTimerTick;
            depthRefreshTimer.Start();

            Closed += GuardianDomWindow_Closed;
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
            Grid.SetColumnSpan(atmPanel, 3);
            selectors.Children.Add(atmPanel);

            Grid.SetRow(selectors, 1);
            root.Children.Add(selectors);

            Grid quotePanel = new Grid
            {
                Margin = new Thickness(8, 0, 8, 6),
                Height = 44,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 34))
            };

            quotePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quotePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quotePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            bidValue = AddQuoteBox(quotePanel, "BID", "--", 0, new SolidColorBrush(Color.FromRgb(31, 55, 82)));
            lastValue = AddQuoteBox(quotePanel, "LAST", "--", 1, new SolidColorBrush(Color.FromRgb(62, 62, 65)));
            askValue = AddQuoteBox(quotePanel, "ASK", "--", 2, new SolidColorBrush(Color.FromRgb(83, 40, 40)));

            Grid.SetRow(quotePanel, 2);
            root.Children.Add(quotePanel);

            Grid columnHeader = new Grid
            {
                Margin = new Thickness(8, 2, 8, 0),
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 34))
            };

            ColumnDefinition headerSellCol = new ColumnDefinition { Width = new GridLength(90) };
            ColumnDefinition headerPriceCol = new ColumnDefinition { Width = new GridLength(100) };
            ColumnDefinition headerBuyCol = new ColumnDefinition { Width = new GridLength(90) };
            ColumnDefinition headerVolumeCol = new ColumnDefinition { Width = new GridLength(75) };
            ColumnDefinition headerDeltaCol = new ColumnDefinition { Width = new GridLength(115) };

            columnHeader.ColumnDefinitions.Add(headerSellCol);
            columnHeader.ColumnDefinitions.Add(headerPriceCol);
            columnHeader.ColumnDefinitions.Add(headerBuyCol);
            columnHeader.ColumnDefinitions.Add(headerVolumeCol);
            columnHeader.ColumnDefinitions.Add(headerDeltaCol);

            AddHeaderCell(columnHeader, "VENDA", 0);
            AddHeaderCell(columnHeader, "PREÇO", 1);
            AddHeaderCell(columnHeader, "COMPRA", 2);
            AddHeaderCell(columnHeader, "VOLUME", 3);
            AddHeaderCell(columnHeader, "DELTA", 4);

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
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 39))
            };

            ColumnDefinition ladderSellCol = new ColumnDefinition { Width = new GridLength(90), MinWidth = 45 };
            ColumnDefinition ladderPriceCol = new ColumnDefinition { Width = new GridLength(100), MinWidth = 60 };
            ColumnDefinition ladderBuyCol = new ColumnDefinition { Width = new GridLength(90), MinWidth = 45 };
            ColumnDefinition ladderVolumeCol = new ColumnDefinition { Width = new GridLength(75), MinWidth = 50 };
            ColumnDefinition ladderDeltaCol = new ColumnDefinition { Width = new GridLength(115), MinWidth = 60 };

            ladder.ColumnDefinitions.Add(ladderSellCol);
            ladder.ColumnDefinitions.Add(ladderPriceCol);
            ladder.ColumnDefinitions.Add(ladderBuyCol);
            ladder.ColumnDefinitions.Add(ladderVolumeCol);
            ladder.ColumnDefinitions.Add(ladderDeltaCol);

            // V0.9.9.35 - dimensionadores independentes no cabeçalho.
            // Não usam GridSplitter e não entram na árvore da ladder.
            ColumnDefinition[] headerCols = new ColumnDefinition[]
            {
                headerSellCol, headerPriceCol, headerBuyCol, headerVolumeCol, headerDeltaCol
            };
            ColumnDefinition[] ladderCols = new ColumnDefinition[]
            {
                ladderSellCol, ladderPriceCol, ladderBuyCol, ladderVolumeCol, ladderDeltaCol
            };
            double[] minWidths = new double[] { 45, 60, 45, 50, 60 };

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

                handle.MouseLeftButtonDown += (s, e) =>
                {
                    dragging = true;
                    startX = e.GetPosition(columnHeader).X;
                    leftStart = headerCols[handleIndex].ActualWidth;
                    rightStart = headerCols[handleIndex + 1].ActualWidth;
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

                    if (newLeft < minWidths[handleIndex])
                    {
                        newLeft = minWidths[handleIndex];
                        newRight = leftStart + rightStart - newLeft;
                    }
                    if (newRight < minWidths[handleIndex + 1])
                    {
                        newRight = minWidths[handleIndex + 1];
                        newLeft = leftStart + rightStart - newRight;
                    }

                    headerCols[handleIndex].Width = new GridLength(newLeft);
                    headerCols[handleIndex + 1].Width = new GridLength(newRight);
                    ladderCols[handleIndex].Width = new GridLength(newLeft);
                    ladderCols[handleIndex + 1].Width = new GridLength(newRight);
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
                ladder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                bidCells[i] = AddLadderCell(ladder, "", i, 2,
                    new SolidColorBrush(Color.FromRgb(25, 92, 48)), out bidBorders[i]);

                priceCells[i] = AddLadderCell(ladder, "--", i, 1,
                    new SolidColorBrush(Color.FromRgb(62, 62, 65)), out priceBorders[i]);

                int previewRow = i;
                priceCells[i].Cursor = Cursors.Hand;
                priceCells[i].MouseLeftButtonDown +=
                    (s, e) => PreviewOrderAtRow(previewRow, "COMPRA");
                priceCells[i].MouseRightButtonDown +=
                    (s, e) => PreviewOrderAtRow(previewRow, "VENDA");

                askCells[i] = AddLadderCell(ladder, "", i, 0,
                    new SolidColorBrush(Color.FromRgb(55, 55, 58)), out askBorders[i]);

                // Barra proporcional COMPRA: cresce da esquerda para a direita.
                buyFlowGrids[i] = new Grid();
                bidBorders[i].Child = null;
                buyFlowBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(25, 112, 55)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0
                };
                buyFlowGrids[i].Children.Add(buyFlowBars[i]);
                buyFlowGrids[i].Children.Add(bidCells[i]);
                bidBorders[i].Child = buyFlowGrids[i];
                bidBorders[i].Background = new SolidColorBrush(Color.FromRgb(55, 55, 58));

                // Barra proporcional VENDA: cresce da direita para a esquerda.
                sellFlowGrids[i] = new Grid();
                askBorders[i].Child = null;
                sellFlowBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(145, 45, 45)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Width = 0
                };
                sellFlowGrids[i].Children.Add(sellFlowBars[i]);
                sellFlowGrids[i].Children.Add(askCells[i]);
                askBorders[i].Child = sellFlowGrids[i];
                askBorders[i].Background = new SolidColorBrush(Color.FromRgb(55, 55, 58));

                volumeCells[i] = AddLadderCell(ladder, "", i, 3,
                    new SolidColorBrush(Color.FromRgb(48, 48, 51)), out volumeBorders[i]);

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
                    Background = new SolidColorBrush(Color.FromRgb(36, 36, 39)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(52, 52, 55)),
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
                    Background = new SolidColorBrush(Color.FromRgb(125, 22, 25)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Width = 0
                };
                Grid.SetColumn(deltaNegativeBars[i], 0);

                deltaPositiveBars[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(20, 105, 35)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Width = 0
                };
                Grid.SetColumn(deltaPositiveBars[i], 1);

                // Mantém deltaCells para compatibilidade com a rotina de limpeza,
                // mas a exibição passa a usar textos espelhados em torno do zero.
                deltaCells[i] = new TextBlock { Text = "", Visibility = Visibility.Collapsed };

                deltaNegativeCells[i] = new TextBlock
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
                Grid.SetColumn(deltaNegativeCells[i], 0);

                deltaPositiveCells[i] = new TextBlock
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
                Grid.SetColumn(deltaPositiveCells[i], 1);

                deltaGrids[i].Children.Add(deltaNegativeBars[i]);
                deltaGrids[i].Children.Add(deltaPositiveBars[i]);
                deltaGrids[i].Children.Add(deltaNegativeCells[i]);
                deltaGrids[i].Children.Add(deltaPositiveCells[i]);
                deltaBorders[i].Child = deltaGrids[i];
            }

            Grid.SetRow(ladder, 4);
            root.Children.Add(ladder);

            Border statusBorder = new Border
            {
                Margin = new Thickness(8, 0, 8, 8),
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 32)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 84)),
                BorderThickness = new Thickness(1)
            };

            connectionStatus = new TextBlock
            {
                Text = "V0.9.9.48 DYNAMIC DELTA HEAT • SELECIONE UM ATIVO • PRÉVIA LOCAL / Sim101",
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
                Content = "▲ +10",
                Width = 72,
                Height = 25,
                Margin = new Thickness(2)
            };
            ladderUpButton.Click += (s, e) =>
            {
                ladderOffsetTicks += 10;
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
                Content = "▼ -10",
                Width = 72,
                Height = 25,
                Margin = new Thickness(2)
            };
            ladderDownButton.Click += (s, e) =>
            {
                ladderOffsetTicks -= 10;
                ladderManualNavigation = true;
                UpdateDisplay();
            };

            navigationPanel.Children.Add(ladderUpButton);
            navigationPanel.Children.Add(ladderCenterButton);
            navigationPanel.Children.Add(ladderDownButton);
            statusPanel.Children.Add(navigationPanel);

            sendPreviewButton = new Button
            {
                Content = "CONFIRMAR PRÉVIA — NÃO ENVIA ORDEM",
                Height = 28,
                Margin = new Thickness(8, 5, 8, 0),
                IsEnabled = false
            };
            sendPreviewButton.Click += SendPreviewButton_Click;
            statusPanel.Children.Add(sendPreviewButton);

            cancelOrderButton = new Button
            {
                Content = "CANCELAR — SEM ORDEM NA V0.9.9.45",
                Height = 28,
                Margin = new Thickness(8, 4, 8, 0),
                IsEnabled = false
            };
            cancelOrderButton.Click += CancelOrderButton_Click;
            statusPanel.Children.Add(cancelOrderButton);

            statusPanel.Children.Add(profileStatus);
            statusBorder.Child = statusPanel;

            Grid.SetRow(statusBorder, 5);
            root.Children.Add(statusBorder);

            return root;
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
            if (!double.TryParse(priceCells[rowIndex].Text,
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
            if (sendPreviewButton == null)
                return;

            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;
            bool sim101 = account != null && string.Equals(account.Name, "Sim101", StringComparison.OrdinalIgnoreCase);
            bool ready = currentInstrument != null
                && !double.IsNaN(previewOrderPrice)
                && previewOrderQuantity > 0
                && (previewOrderType == "LIMIT" || previewOrderType == "STOP MARKET");

            sendPreviewButton.IsEnabled = sim101 && ready;
        }

        // V0.9.9.45 - confirmação 100% local da prévia.
        // IMPORTANTE: esta rotina NÃO cria, NÃO submete e NÃO inicia qualquer ordem/ATM.
        private void SendPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;

            if (account == null || !string.Equals(account.Name, "Sim101", StringComparison.OrdinalIgnoreCase))
            {
                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • BLOQUEADO: SOMENTE Sim101";
                return;
            }

            if (currentInstrument == null || double.IsNaN(previewOrderPrice) || previewOrderQuantity <= 0)
                return;

            guardianOrderStatus = "PRÉVIA CONFIRMADA";

            if (orderStateStatus != null)
            {
                orderStateStatus.Text = "PRÉVIA CONFIRMADA • NÃO ENVIADA";
                orderStateStatus.Foreground = Brushes.Gold;
            }

            connectionStatus.Text =
                "V0.9.9.48 DYNAMIC DELTA HEAT • CONFIRMADA: " + previewOrderSide +
                " " + previewOrderQuantity + " @ " +
                currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice) +
                " • " + previewOrderType + " • NENHUMA ORDEM ENVIADA";

            // Mantém a seleção ativa para permitir revisar/alterar o nível.
            UpdateSendButtonState();
        }

        private void CancelOrderButton_Click(object sender, RoutedEventArgs e)
        {
            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;

            if (account == null || !string.Equals(account.Name, "Sim101", StringComparison.OrdinalIgnoreCase))
            {
                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • CANCELAMENTO BLOQUEADO: SOMENTE Sim101";
                return;
            }

            if (guardianSubmittedOrder == null)
            {
                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • NENHUMA ORDEM DESTA INSTÂNCIA PARA CANCELAR";
                return;
            }

            try
            {
                account.Cancel(new[] { guardianSubmittedOrder });
                guardianOrderStatus = "CANCELAMENTO SOLICITADO";
                if (orderStateStatus != null)
                    orderStateStatus.Text = "ORDEM: CANCELANDO";
                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • CANCELAMENTO SOLICITADO • aguardando confirmação";
                cancelOrderButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • ERRO AO CANCELAR: " + ex.Message;
            }
        }

        private void SubmitProtectiveBracket(Order filledEntry)
        {
            if (filledEntry == null || guardianOrderAccount == null || bracketSubmittedForEntry)
                return;

            if (!string.Equals(guardianOrderAccount.Name, "Sim101", StringComparison.OrdinalIgnoreCase))
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
                "V0.9.9.48 DYNAMIC DELTA HEAT • STOP " +
                filledEntry.Instrument.MasterInstrument.FormatPrice(stopPrice) +
                " • ALVO " +
                filledEntry.Instrument.MasterInstrument.FormatPrice(targetPrice) +
                " • OCO • Sim101";
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
                            ? "V0.9.9.48 DYNAMIC DELTA HEAT • POSIÇÃO ENCERRADA PELO STOP • OCO"
                            : "V0.9.9.48 DYNAMIC DELTA HEAT • POSIÇÃO ENCERRADA PELO ALVO • OCO";
                    }
                    else if (state == OrderState.Rejected)
                    {
                        if (orderStateStatus != null)
                        {
                            orderStateStatus.Text = isStop ? "STOP REJEITADO" : "ALVO REJEITADO";
                            orderStateStatus.Foreground = Brushes.OrangeRed;
                        }
                        connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • ORDEM DE PROTEÇÃO REJEITADA";
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
                    cancelOrderButton.IsEnabled = cancellable;

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

                connectionStatus.Text =
                    "V0.9.9.48 DYNAMIC DELTA HEAT • ORDEM: " + statePt +
                    " • " + previewOrderSide + " " + previewOrderQuantity +
                    " @ " + (currentInstrument == null ? "--" :
                        currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice)) +
                    " • Sim101";
            }));
        }

        private void UpdateOrderPreviewVisual()
        {
            for (int i = 0; i < LadderRows; i++)
            {
                priceBorders[i].BorderBrush =
                    new SolidColorBrush(Color.FromRgb(58, 58, 61));
                priceBorders[i].BorderThickness = new Thickness(0.5);
            }

            if (double.IsNaN(previewOrderPrice) || currentInstrument == null)
                return;

            double tick = currentInstrument.MasterInstrument.TickSize;

            for (int i = 0; i < LadderRows; i++)
            {
                double rowPrice;
                if (!double.TryParse(priceCells[i].Text,
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
                "V0.9.9.48 DYNAMIC DELTA HEAT • " + previewOrderSide +
                " " + previewOrderQuantity.ToString() +
                " @ " + currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice) +
                " • " + previewOrderType +
                " • PRÉVIA PRONTA • CONFIRMAÇÃO LOCAL / Sim101";
        }

        private void OnDepthRefreshTimerTick(object sender, EventArgs e)
        {
            if (currentInstrument == null)
                return;

            // V0.9.9.40 PERFORMANCE:
            // processamento de mercado continua acumulando todos os negócios,
            // mas perfil + interface são consolidados no timer (10 Hz).
            // Evita redesenhar toda a ladder a cada evento de market data.
            RefreshDailyProfile();
            RecalculateDailyProfile();
            UpdateDisplay();
        }

        private void OnInstrumentChanged(object sender, EventArgs e)
        {
            Instrument selected = instrumentSelector != null ? instrumentSelector.Instrument : null;
            ChangeInstrument(selected);
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
                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • SELECIONE UM ATIVO • PRÉVIA LOCAL / Sim101";
                return;
            }

            connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • CONECTANDO MARKET DATA • PRÉVIA LOCAL / Sim101";

            marketData = new MarketData(currentInstrument);
            marketData.Update += OnMarketData;

            if (marketData.Bid != null)
                bidPrice = marketData.Bid.Price;

            if (marketData.Ask != null)
                askPrice = marketData.Ask.Price;

            if (marketData.Last != null)
                lastPrice = marketData.Last.Price;

            LoadHistoricalProfile();
            UpdateDisplay();
        }

        // V0.9.9.22: carrega o historico de negocios de 1 tick do dia.
        // Isso permite iniciar POC/VAH/VAL sem esperar a janela acumular do zero.
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
            double price = e.Price;
            long volume = e.Volume;
            MarketDataType type = e.MarketDataType;

            // Captura o inside market diretamente do MarketData.
            double eventBid = bidPrice;
            double eventAsk = askPrice;

            if (marketData != null)
            {
                if (marketData.Bid != null && marketData.Bid.Price > 0)
                    eventBid = marketData.Bid.Price;

                if (marketData.Ask != null && marketData.Ask.Price > 0)
                    eventAsk = marketData.Ask.Price;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (currentInstrument == null)
                    return;

                if (type == MarketDataType.Bid)
                {
                    bidPrice = price;
                }
                else if (type == MarketDataType.Ask)
                {
                    askPrice = price;
                }
                else if (type == MarketDataType.Last)
                {
                    lastPrice = price;

                    if (eventBid > 0)
                        bidPrice = eventBid;
                    if (eventAsk > 0)
                        askPrice = eventAsk;

                    AccumulateTrade(price, volume, eventAsk, eventBid);
                }

                // V0.9.9.40: sem UpdateDisplay() por evento.
                // A interface é atualizada pelo timer de 100 ms.
            }));
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
            flowTrades++;
        }

        private void RefreshDailyProfile()
        {
            if (currentInstrument == null)
                return;

            long version = GuardianVolumeBridge.Versao;
            if (version == lastVolumeBridgeVersion)
                return;

            Dictionary<double, long> snapshot =
                GuardianVolumeBridge.Snapshot(currentInstrument.FullName);

            // Bridge externo continua tendo prioridade. Sem ele, mantém o
            // perfil local acumulado pelos negócios recebidos nesta janela.
            if (snapshot.Count == 0)
            {
                if (dailyVolume.Count > 0)
                    RecalculateDailyProfile();
                return;
            }

            dailyVolume.Clear();
            foreach (KeyValuePair<double, long> kv in snapshot)
            {
                double price = currentInstrument.MasterInstrument.RoundToTickSize(kv.Key);
                long current;
                dailyVolume.TryGetValue(price, out current);
                dailyVolume[price] = current + kv.Value;
            }

            lastVolumeBridgeVersion = version;
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
                                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • ERRO BE1: " + ex.Message;
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

        private void UpdateDisplay()
        {
            MonitorBreakevenTriggers();
            if (currentInstrument == null)
                return;

            bidValue.Text = FormatPrice(bidPrice);
            askValue.Text = FormatPrice(askPrice);
            lastValue.Text = FormatPrice(lastPrice);

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
                connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • AGUARDANDO COTAÇÃO • ENVIO SOMENTE POR BOTÃO / Sim101";
                return;
            }

            connectionStatus.Text = "V0.9.9.48 DYNAMIC DELTA HEAT • NEGÓCIOS: " + flowTrades.ToString()
                + " • PERFIL: " + (dailyVolume.Count > 0
                    ? (lastVolumeBridgeVersion >= 0 ? "VOLUMEPRO OK"
                        : (lastVolumeBridgeVersion == -2 ? "SESSÃO 19H + AO VIVO" : "LOCAL AO VIVO"))
                    : "CARREGANDO HISTÓRICO")
                + (ladderManualNavigation ? " • LADDER: MANUAL " + (ladderOffsetTicks >= 0 ? "+" : "") + ladderOffsetTicks.ToString() + "t" : " • LADDER: AUTO")
                + " • ENVIO SOMENTE POR BOTÃO / Sim101";

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
                    int triggerTicks = CenterRow - AutoCenterEdgeRows; // 21 linhas: dispara a 5 ticks do centro
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

                priceCells[row].Text = currentInstrument.MasterInstrument.FormatPrice(levelPrice);

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
                    deltaNegativeBars[row].Opacity = delta < 0 ? 0.16 + (0.84 * deltaHeat) : 0.0;
                    deltaPositiveBars[row].Opacity = delta > 0 ? 0.16 + (0.84 * deltaHeat) : 0.0;

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
                buyFlowBars[row].Opacity = buyVolume > 0 ? 0.16 + (0.84 * buyHeat) : 0.0;
                sellFlowBars[row].Opacity = sellVolume > 0 ? 0.16 + (0.84 * sellHeat) : 0.0;

                // V0.9.9.3 ATM: o preço não depende de MarketDepth.
                // A leitura visual do fluxo fica nas colunas COMPRA/VENDA.
                Brush priceBackground = new SolidColorBrush(Color.FromRgb(62, 62, 65));

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

                    bidBorders[row].Background = activeRow;
                    priceBorders[row].Background = activeRow;
                    askBorders[row].Background = activeRow;

                    bidCells[row].Foreground = Brushes.Black;
                    priceCells[row].Foreground = Brushes.Black;
                    askCells[row].Foreground = Brushes.Black;
                    volumeCells[row].Foreground = Brushes.White;

                    priceCells[row].FontWeight = FontWeights.Bold;
                }
                else
                {
                    Brush neutralSide = new SolidColorBrush(Color.FromRgb(55, 55, 58));
                    Brush bidMarker = new SolidColorBrush(Color.FromRgb(30, 73, 105));
                    Brush askMarker = new SolidColorBrush(Color.FromRgb(105, 45, 45));

                    bidBorders[row].Background = isBidRow ? bidMarker : neutralSide;
                    priceBorders[row].Background = priceBackground;
                    askBorders[row].Background = isAskRow ? askMarker : neutralSide;

                    bidCells[row].Foreground = Brushes.WhiteSmoke;
                    priceCells[row].Foreground = Brushes.WhiteSmoke;
                    askCells[row].Foreground = Brushes.WhiteSmoke;
                    volumeCells[row].Foreground = Brushes.WhiteSmoke;

                    priceCells[row].FontWeight = FontWeights.Normal;
                }
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

                bidBorders[i].Background = new SolidColorBrush(Color.FromRgb(55, 55, 58));
                priceBorders[i].Background = new SolidColorBrush(Color.FromRgb(62, 62, 65));
                askBorders[i].Background = new SolidColorBrush(Color.FromRgb(55, 55, 58));

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

            buyFlow.Clear();
            sellFlow.Clear();
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

            if (depthRefreshTimer != null)
            {
                depthRefreshTimer.Stop();
                depthRefreshTimer.Tick -= OnDepthRefreshTimerTick;
                depthRefreshTimer = null;
            }

            if (instrumentSelector != null)
                instrumentSelector.InstrumentChanged -= OnInstrumentChanged;

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
                Padding = new Thickness(4)
            };

            StackPanel panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.LightGray,
                FontSize = 9,
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

        private void AddHeaderCell(Grid grid, string text, int column)
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
        }

        private TextBlock AddLadderCell(Grid grid, string text, int row, int column, Brush background, out Border border)
        {
            border = new Border
            {
                Background = background,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 61)),
                BorderThickness = new Thickness(0.5)
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
