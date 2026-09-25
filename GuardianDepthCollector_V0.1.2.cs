#region Using declarations
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
#endregion

namespace NinjaTrader.NinjaScript.AddOns.GuardianDOM
{
    public class GuardianDepthCollector : AddOnBase
    {
        private NTMenuItem menuItem;

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter controlCenter = window as ControlCenter;
            if (controlCenter == null)
                return;

            menuItem = new NTMenuItem
            {
                Header = "Guardian Depth Collector",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            menuItem.Click += OnMenuItemClick;

            NTMenuItem newMenu = controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (newMenu != null)
                newMenu.Items.Add(menuItem);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            ControlCenter controlCenter = window as ControlCenter;
            if (controlCenter == null || menuItem == null)
                return;

            menuItem.Click -= OnMenuItemClick;

            NTMenuItem newMenu = controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (newMenu != null)
                newMenu.Items.Remove(menuItem);

            menuItem = null;
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            new GuardianDepthCollectorWindow().Show();
        }
    }

    public class GuardianDepthCollectorWindow : NTWindow
    {
        private Instrument instrument;
        private DispatcherTimer uiTimer;

        private TextBlock statusText;
        private TextBlock eventsText;
        private TextBlock bidText;
        private TextBlock askText;

        private long totalEvents;
        private long bidEvents;
        private long askEvents;

        private double lastBidPrice;
        private long lastBidVolume;
        private double lastAskPrice;
        private long lastAskVolume;

        public GuardianDepthCollectorWindow()
        {
            Caption = "Guardian Depth Collector V0.1.2";
            Width = 430;
            Height = 260;

            Grid grid = new Grid
            {
                Margin = new Thickness(16)
            };

            for (int i = 0; i < 6; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            statusText = AddLine(grid, 0, "STATUS: iniciando");
            eventsText = AddLine(grid, 1, "EVENTOS: 0");
            bidText = AddLine(grid, 2, "BID: aguardando");
            askText = AddLine(grid, 3, "ASK: aguardando");

            TextBlock note = AddLine(
                grid,
                4,
                "Fonte: Instrument.MarketDepth (fora do SuperDOM)");

            note.Margin = new Thickness(0, 14, 0, 0);

            Content = grid;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private TextBlock AddLine(Grid grid, int row, string text)
        {
            TextBlock block = new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 4, 0, 4),
                FontSize = 14
            };

            Grid.SetRow(block, row);
            grid.Children.Add(block);
            return block;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Diagnostic version intentionally fixed to the instrument currently
            // being tested. Once validated, GuardianDOM can pass its own instrument.
            instrument = Instrument.GetInstrument("MGC DEC26");

            if (instrument == null)
            {
                statusText.Text = "STATUS: instrumento não encontrado";
                return;
            }

            statusText.Text = "STATUS: assinando " + instrument.FullName;

            // NinjaTrader's AddOn pattern: subscribe/unsubscribe MarketDepth
            // on the Instrument's own Dispatcher.
            instrument.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (instrument != null && instrument.MarketDepth != null)
                    instrument.MarketDepth.Update += OnMarketDepthUpdate;
            }));

            uiTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            uiTimer.Tick += RefreshUi;
            uiTimer.Start();
        }

        private void OnMarketDepthUpdate(object sender, MarketDepthEventArgs e)
        {
            if (e == null)
                return;

            if (e.IsReset)
                return;

            totalEvents++;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                bidEvents++;
                lastBidPrice = e.Price;
                lastBidVolume = e.Operation == Operation.Remove ? 0 : e.Volume;
            }
            else if (e.MarketDataType == MarketDataType.Ask)
            {
                askEvents++;
                lastAskPrice = e.Price;
                lastAskVolume = e.Operation == Operation.Remove ? 0 : e.Volume;
            }
        }

        private void RefreshUi(object sender, EventArgs e)
        {
            statusText.Text = "STATUS: ativo • " + instrument.FullName;
            eventsText.Text =
                "EVENTOS: " + totalEvents +
                "   BID: " + bidEvents +
                "   ASK: " + askEvents;

            bidText.Text = lastBidPrice > 0
                ? "ÚLTIMO BID: " + lastBidPrice + " x " + lastBidVolume
                : "ÚLTIMO BID: aguardando";

            askText.Text = lastAskPrice > 0
                ? "ÚLTIMO ASK: " + lastAskPrice + " x " + lastAskVolume
                : "ÚLTIMO ASK: aguardando";
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (uiTimer != null)
            {
                uiTimer.Stop();
                uiTimer.Tick -= RefreshUi;
                uiTimer = null;
            }

            Instrument instrumentToDetach = instrument;
            instrument = null;

            if (instrumentToDetach != null)
            {
                instrumentToDetach.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (instrumentToDetach.MarketDepth != null)
                        instrumentToDetach.MarketDepth.Update -= OnMarketDepthUpdate;
                }));
            }

            Loaded -= OnLoaded;
            Closed -= OnClosed;
        }
    }
}
