#region Using declarations
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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

        private Instrument currentInstrument;
        private MarketData marketData;
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

        private double lastPrice;
        private double bidPrice;
        private double askPrice;

        private const int LadderRows = 21;
        private const int CenterRow = 10;

        // Navegação manual da ladder. Zero = acompanha o mercado.
        private int ladderOffsetTicks = 0;
        private bool ladderManualNavigation = false;

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

            StackPanel instrumentPanel = CreateSelectorPanel("ATIVO");
            instrumentSelector = new InstrumentSelector
            {
                Margin = new Thickness(0, 4, 4, 0)
            };
            instrumentSelector.InstrumentChanged += OnInstrumentChanged;
            instrumentPanel.Children.Add(instrumentSelector);
            Grid.SetColumn(instrumentPanel, 0);
            selectors.Children.Add(instrumentPanel);

            StackPanel accountPanel = CreateSelectorPanel("CONTA");
            accountSelector = new AccountSelector
            {
                Margin = new Thickness(4, 4, 4, 0)
            };
            accountPanel.Children.Add(accountSelector);
            Grid.SetColumn(accountPanel, 1);
            selectors.Children.Add(accountPanel);

            StackPanel quantityPanel = CreateSelectorPanel("QTD");
            quantitySelector = new QuantityUpDown
            {
                Value = 1,
                Margin = new Thickness(4, 4, 0, 0)
            };
            quantityPanel.Children.Add(quantitySelector);
            Grid.SetColumn(quantityPanel, 2);
            selectors.Children.Add(quantityPanel);

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

            columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

            AddHeaderCell(columnHeader, "COMPRA", 0);
            AddHeaderCell(columnHeader, "PREÇO", 1);
            AddHeaderCell(columnHeader, "VENDA", 2);
            AddHeaderCell(columnHeader, "VOLUME", 3);

            Grid.SetRow(columnHeader, 3);
            root.Children.Add(columnHeader);

            ladder = new Grid
            {
                Margin = new Thickness(8, 0, 8, 8),
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 39))
            };

            ladder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ladder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            ladder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ladder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

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

            for (int i = 0; i < LadderRows; i++)
            {
                ladder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                bidCells[i] = AddLadderCell(ladder, "", i, 0,
                    new SolidColorBrush(Color.FromRgb(25, 92, 48)), out bidBorders[i]);

                priceCells[i] = AddLadderCell(ladder, "--", i, 1,
                    new SolidColorBrush(Color.FromRgb(62, 62, 65)), out priceBorders[i]);

                int previewRow = i;
                priceCells[i].Cursor = Cursors.Hand;
                priceCells[i].MouseLeftButtonDown +=
                    (s, e) => PreviewOrderAtRow(previewRow, "COMPRA");
                priceCells[i].MouseRightButtonDown +=
                    (s, e) => PreviewOrderAtRow(previewRow, "VENDA");

                askCells[i] = AddLadderCell(ladder, "", i, 2,
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
                Text = "V0.9.8.3 BE1 ONLY • SELECIONE UM ATIVO • ENVIO SOMENTE POR BOTÃO / Sim101",
                Foreground = Brushes.Gold,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            StackPanel statusPanel = new StackPanel();

            statusPanel.Children.Add(connectionStatus);

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
                Content = "ENVIAR PRÉVIA — SOMENTE Sim101",
                Height = 28,
                Margin = new Thickness(8, 5, 8, 0),
                IsEnabled = false
            };
            sendPreviewButton.Click += SendPreviewButton_Click;
            statusPanel.Children.Add(sendPreviewButton);

            cancelOrderButton = new Button
            {
                Content = "CANCELAR ORDEM DO GUARDIANDOM",
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

        private void SendPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;

            if (account == null || !string.Equals(account.Name, "Sim101", StringComparison.OrdinalIgnoreCase))
            {
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • BLOQUEADO: SOMENTE Sim101";
                return;
            }

            if (currentInstrument == null || double.IsNaN(previewOrderPrice) || previewOrderQuantity <= 0)
                return;

            OrderAction action = previewOrderSide == "COMPRA" ? OrderAction.Buy : OrderAction.SellShort;
            OrderType type = previewOrderType == "STOP MARKET" ? OrderType.StopMarket : OrderType.Limit;
            double limitPrice = type == OrderType.Limit ? previewOrderPrice : 0;
            double stopPrice = type == OrderType.StopMarket ? previewOrderPrice : 0;

            try
            {
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
                    "GuardianDOM",
                    Core.Globals.MaxDate,
                    null);

                // Nova entrada: zera apenas as referências do bracket desta entrada.
                guardianStopOrder = null;
                guardianTargetOrder = null;
                bracketSubmittedForEntry = false;
                beMonitorActive = false;
                be20Detected = false;
                be1ChangeSent = false;
                be40Detected = false;
                beEntryFillPrice = double.NaN;
                beEntryDirection = 0;

                account.Submit(new[] { order });
                guardianSubmittedOrder = order;
                guardianOrderStatus = "ENVIADA";
                if (orderStateStatus != null)
                    orderStateStatus.Text = "ORDEM: ENVIADA";

                if (guardianOrderAccount != account)
                {
                    if (guardianOrderAccount != null)
                        guardianOrderAccount.OrderUpdate -= GuardianOrderAccount_OrderUpdate;

                    guardianOrderAccount = account;
                    guardianOrderAccount.OrderUpdate += GuardianOrderAccount_OrderUpdate;
                }

                if (cancelOrderButton != null)
                    cancelOrderButton.IsEnabled = true;

                connectionStatus.Text =
                    "V0.9.8.3 BE1 ONLY • ENVIADA: " + previewOrderSide +
                    " " + previewOrderQuantity + " @ " +
                    currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice) +
                    " • " + previewOrderType + " • Sim101";

                sendPreviewButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • ERRO AO ENVIAR: " + ex.Message;
            }
        }

        private void CancelOrderButton_Click(object sender, RoutedEventArgs e)
        {
            Account account = accountSelector == null ? null : accountSelector.SelectedAccount;

            if (account == null || !string.Equals(account.Name, "Sim101", StringComparison.OrdinalIgnoreCase))
            {
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • CANCELAMENTO BLOQUEADO: SOMENTE Sim101";
                return;
            }

            if (guardianSubmittedOrder == null)
            {
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • NENHUMA ORDEM DESTA INSTÂNCIA PARA CANCELAR";
                return;
            }

            try
            {
                account.Cancel(new[] { guardianSubmittedOrder });
                guardianOrderStatus = "CANCELAMENTO SOLICITADO";
                if (orderStateStatus != null)
                    orderStateStatus.Text = "ORDEM: CANCELANDO";
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • CANCELAMENTO SOLICITADO • aguardando confirmação";
                cancelOrderButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • ERRO AO CANCELAR: " + ex.Message;
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
                "V0.9.8.3 BE1 ONLY • STOP " +
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

            // A entrada confirmada cria o bracket apenas uma vez.
            if (isEntry && e.Order.OrderState == OrderState.Filled && !bracketSubmittedForEntry)
            {
                try
                {
                    SubmitProtectiveBracket(e.Order);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (orderStateStatus != null)
                        {
                            orderStateStatus.Text = "ERRO AO CRIAR STOP/ALVO";
                            orderStateStatus.Foreground = Brushes.OrangeRed;
                        }
                        connectionStatus.Text = "V0.9.8.3 BE1 ONLY • ERRO: " + ex.Message;
                    }));
                }
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
                            ? "V0.9.8.3 BE1 ONLY • POSIÇÃO ENCERRADA PELO STOP • OCO"
                            : "V0.9.8.3 BE1 ONLY • POSIÇÃO ENCERRADA PELO ALVO • OCO";
                    }
                    else if (state == OrderState.Rejected)
                    {
                        if (orderStateStatus != null)
                        {
                            orderStateStatus.Text = isStop ? "STOP REJEITADO" : "ALVO REJEITADO";
                            orderStateStatus.Foreground = Brushes.OrangeRed;
                        }
                        connectionStatus.Text = "V0.9.8.3 BE1 ONLY • ORDEM DE PROTEÇÃO REJEITADA";
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
                        statePt = bracketSubmittedForEntry ? "EXECUTADA + PROTEGIDA" : "EXECUTADA";
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
                        ? "POSIÇÃO PROTEGIDA • STOP 40t • ALVO 50t"
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
                    "V0.9.8.3 BE1 ONLY • ORDEM: " + statePt +
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
                "V0.9.8.3 BE1 ONLY • " + previewOrderSide +
                " " + previewOrderQuantity.ToString() +
                " @ " + currentInstrument.MasterInstrument.FormatPrice(previewOrderPrice) +
                " • " + previewOrderType +
                " • PRÉVIA PRONTA • ENVIO SOMENTE POR BOTÃO / Sim101";
        }

        private void OnDepthRefreshTimerTick(object sender, EventArgs e)
        {
            if (currentInstrument == null)
                return;

            RefreshDailyProfile();
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
            lastPrice = 0;
            bidPrice = 0;
            askPrice = 0;

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
            previewOrderPrice = double.NaN;
            previewOrderQuantity = 0;
            previewOrderSide = string.Empty;
            previewOrderType = string.Empty;
            ClearQuotesAndLadder();

            if (currentInstrument == null)
            {
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • SELECIONE UM ATIVO • ENVIO SOMENTE POR BOTÃO / Sim101";
                return;
            }

            connectionStatus.Text = "V0.9.8.3 BE1 ONLY • CONECTANDO MARKET DATA • ENVIO SOMENTE POR BOTÃO / Sim101";

            marketData = new MarketData(currentInstrument);
            marketData.Update += OnMarketData;

            if (marketData.Bid != null)
                bidPrice = marketData.Bid.Price;

            if (marketData.Ask != null)
                askPrice = marketData.Ask.Price;

            if (marketData.Last != null)
                lastPrice = marketData.Last.Price;

            UpdateDisplay();
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

                UpdateDisplay();
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

            if (snapshot.Count == 0)
                return;

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

            if (dailyVolume.Count == 0)
                return;

            long total = 0;
            foreach (KeyValuePair<double, long> kv in dailyVolume)
            {
                total += kv.Value;
                if (kv.Value > maxDailyVolume)
                {
                    maxDailyVolume = kv.Value;
                    profilePoc = kv.Key;
                }
            }

            if (total <= 0 || double.IsNaN(profilePoc))
                return;

            List<double> prices = new List<double>(dailyVolume.Keys);
            prices.Sort();

            int pocIndex = prices.FindIndex(p =>
                Math.Abs(p - profilePoc) < currentInstrument.MasterInstrument.TickSize * 0.5);

            if (pocIndex < 0)
                return;

            long target = (long)Math.Ceiling(total * 0.70);
            long accumulated = dailyVolume[prices[pocIndex]];
            int low = pocIndex;
            int high = pocIndex;

            while (accumulated < target && (low > 0 || high < prices.Count - 1))
            {
                long below = low > 0 ? dailyVolume[prices[low - 1]] : -1;
                long above = high < prices.Count - 1 ? dailyVolume[prices[high + 1]] : -1;

                if (above >= below && above >= 0)
                {
                    high++;
                    accumulated += dailyVolume[prices[high]];
                }
                else if (below >= 0)
                {
                    low--;
                    accumulated += dailyVolume[prices[low]];
                }
                else
                    break;
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
                                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • ERRO BE1: " + ex.Message;
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

            // V0.9.8.3 BE1 ONLY: keep the ladder centered on the live inside market.
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
                connectionStatus.Text = "V0.9.8.3 BE1 ONLY • AGUARDANDO COTAÇÃO • ENVIO SOMENTE POR BOTÃO / Sim101";
                return;
            }

            connectionStatus.Text = "V0.9.8.3 BE1 ONLY • NEGÓCIOS: " + flowTrades.ToString()
                + " • PERFIL: " + (dailyVolume.Count > 0 ? "OK" : "AGUARDANDO VOLUMEPRO")
                + (ladderManualNavigation ? " • LADDER: MANUAL " + (ladderOffsetTicks >= 0 ? "+" : "") + ladderOffsetTicks.ToString() + "t" : " • LADDER: AUTO")
                + " • ENVIO SOMENTE POR BOTÃO / Sim101";

            double tickSize = currentInstrument.MasterInstrument.TickSize;
            double marketCenter = currentInstrument.MasterInstrument.RoundToTickSize(anchor);
            double center = currentInstrument.MasterInstrument.RoundToTickSize(
                marketCenter + ladderOffsetTicks * tickSize);

            long maxBuyVisible = 0;
            long maxSellVisible = 0;

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

                // Largura relativa ao maior volume visível de cada lado.
                double buyRatio = maxBuyVisible > 0 ? Math.Min(1.0, (double)buyVolume / maxBuyVisible) : 0.0;
                double sellRatio = maxSellVisible > 0 ? Math.Min(1.0, (double)sellVolume / maxSellVisible) : 0.0;

                double buyWidth = bidBorders[row].ActualWidth * buyRatio;
                double sellWidth = askBorders[row].ActualWidth * sellRatio;

                buyFlowBars[row].Width = Math.Max(0, buyWidth);
                sellFlowBars[row].Width = Math.Max(0, sellWidth);

                // V0.9.8.3 BE1 ONLY: o preço não depende de MarketDepth.
                // A leitura visual do fluxo fica nas colunas COMPRA/VENDA.
                Brush priceBackground = new SolidColorBrush(Color.FromRgb(62, 62, 65));

                long daily = 0;
                dailyVolume.TryGetValue(levelPrice, out daily);
                volumeCells[row].Text = daily > 0 ? daily.ToString() : "";

                double volumeRatio = maxDailyVolume > 0
                    ? Math.Min(1.0, (double)daily / maxDailyVolume)
                    : 0.0;

                // Mantém o mesmo princípio visual do Guardian Volume estável:
                // raiz quadrada evita comprimir demais os níveis menores.
                double visualRatio = Math.Sqrt(volumeRatio);
                volumeBars[row].Width = Math.Max(0,
                    volumeBorders[row].ActualWidth * visualRatio);

                Brush normalVolumeColor = new SolidColorBrush(Color.FromRgb(105, 105, 110));
                if (SameProfilePrice(levelPrice, profilePoc))
                    volumeBars[row].Background = new SolidColorBrush(Color.FromRgb(225, 45, 45));
                else if (SameProfilePrice(levelPrice, profileVah) ||
                         SameProfilePrice(levelPrice, profileVal))
                    volumeBars[row].Background = new SolidColorBrush(Color.FromRgb(230, 190, 35));
                else
                    volumeBars[row].Background = normalVolumeColor;

                // A linha do LAST ganha prioridade visual e atravessa BID + PREÇO + ASK.
                bool isLastRow = lastPrice > 0 &&
                    Math.Abs(levelPrice - currentInstrument.MasterInstrument.RoundToTickSize(lastPrice))
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
                    bidBorders[row].Background = new SolidColorBrush(Color.FromRgb(55, 55, 58));
                    priceBorders[row].Background = priceBackground;
                    askBorders[row].Background = new SolidColorBrush(Color.FromRgb(55, 55, 58));

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
