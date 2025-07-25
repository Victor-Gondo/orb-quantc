#region imports
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Globalization;
    using System.Drawing;
    using QuantConnect;
    using QuantConnect.Algorithm.Framework;
    using QuantConnect.Algorithm.Framework.Selection;
    using QuantConnect.Algorithm.Framework.Alphas;
    using QuantConnect.Algorithm.Framework.Portfolio;
    using QuantConnect.Algorithm.Framework.Execution;
    using QuantConnect.Algorithm.Framework.Risk;
    using QuantConnect.Algorithm.Selection;
    using QuantConnect.Parameters;
    using QuantConnect.Benchmarks;
    using QuantConnect.Brokerages;
    using QuantConnect.Util;
    using QuantConnect.Interfaces;
    using QuantConnect.Algorithm;
    using QuantConnect.Indicators;
    using QuantConnect.Data;
    using QuantConnect.Data.Consolidators;
    using QuantConnect.Data.Custom;
    using QuantConnect.DataSource;
    using QuantConnect.Data.Fundamental;
    using QuantConnect.Data.Market;
    using QuantConnect.Data.UniverseSelection;
    using QuantConnect.Notifications;
    using QuantConnect.Orders;
    using QuantConnect.Orders.Fees;
    using QuantConnect.Orders.Fills;
    using QuantConnect.Orders.Slippage;
    using QuantConnect.Scheduling;
    using QuantConnect.Securities;
    using QuantConnect.Securities.Equity;
    using QuantConnect.Securities.Future;
    using QuantConnect.Securities.Option;
    using QuantConnect.Securities.Forex;
    using QuantConnect.Securities.Crypto;   
    using QuantConnect.Securities.Interfaces;
    using QuantConnect.Storage;
    using QCAlgorithmFramework = QuantConnect.Algorithm.QCAlgorithm;
    using QCAlgorithmFrameworkBridge = QuantConnect.Algorithm.QCAlgorithm;
#endregion
namespace QuantConnect.Algorithm.CSharp
{
    public class OpeningRangeBreakoutUniverseAlgorithm : QCAlgorithm
    {
        // parameters
        [Parameter("MaxPositions")]
        public int MaxPositions = 15;
        [Parameter("universeSize")]
        private int _universeSize = 7000;
        [Parameter("excludeETFs")]
        private int _excludeETFs = 1;
        [Parameter("atrThreshold")]
        private decimal _atrThreshold = 0.5m;
        [Parameter("indicatorPeriod")]
        private int _indicatorPeriod = 14; // days
        [Parameter("openingRangeMinutes")]
        private int _openingRangeMinutes = 3;       // when to place entries
        [Parameter("entryBarMinutes")]
        private int _entryBarMinutes = 1; // bar interval for entry confirmation
        [Parameter("stopLossAtrDistance")]      
        public decimal stopLossAtrDistance = 0.1m;  // distance for stop loss, fraction of ATR
        [Parameter("stopLossRiskSize")]
        public decimal stopLossRiskSize = 0.01m; // 0.01 => Lose maximum of 1% of the portfolio if stop loss is hit
        [Parameter("reversing")]
        public int reversing = 1;           // on stop loss also open reverse position and place stop loss at the original entry price
        [Parameter("maximisePositions")]
        private int _maximisePositions = 0; // sends twice as much entry orders, cancel remaining orders when all positions are filled
        [Parameter("secondsResolution")]
        private int _secondsResolution = 0; // switch to seconds resolution for more precision [SLOW!]
        // todo: implement doubling
        [Parameter("doubling")]             // double position when in profit, not ready yet
        private int _doubling = 0;
        [Parameter("fees")]                 // enable or disable broker fees
        
        private int _fees = 0;

        private int _leverage = 0;
        private Universe _universe;
        private int _maxLongPositions = 0;
        private int _maxShortPositions = 0;
        private int _maxPositions = 0;
        private decimal _maxMarginUsed = 0.0m;

        private Dictionary<Symbol, SymbolData> _symbolDataBySymbol = new();

        public override void Initialize()
        {
            SetStartDate(2024, 1, 1);
            SetEndDate(2024, 02, 1);
            SetCash(10_000);
            Settings.AutomaticIndicatorWarmUp = true;
            if (_fees == 0) {
                SetBrokerageModel(BrokerageName.Alpaca); 
            }

            // Add SPY so there is at least 1 asset at minute resolution to step the algorithm along.
            var spy = AddEquity("SPY").Symbol;

            // Add a universe of the most liquid US Equities.
            UniverseSettings.Leverage = _leverage;
            if (_secondsResolution == 1) UniverseSettings.Resolution = Resolution.Second;
            UniverseSettings.Asynchronous = true;
            UniverseSettings.Schedule.On(DateRules.MonthStart(spy));
            _universe = AddUniverse(fundamentals => fundamentals
                .Where(f => f.Price > 5 && (_excludeETFs == 0 || f.HasFundamentalData) && f.Symbol != spy) // && f.MarketCap < ???
                .OrderByDescending(f => f.DollarVolume)
                .Take(_universeSize)
                .Select(f => f.Symbol)
                .ToList()
            );

            Schedule.On(DateRules.EveryDay(spy), TimeRules.AfterMarketOpen(spy, 0), () => ResetVars());
            Schedule.On(DateRules.EveryDay(spy), TimeRules.BeforeMarketClose(spy, 1), () => Liquidate());  // Close all the open positions and cancel standing orders.
            Schedule.On(DateRules.EveryDay(spy), TimeRules.BeforeMarketClose(spy, 1), () => UpdatePlots());
            SetWarmUp(TimeSpan.FromDays(2 * _indicatorPeriod));

            Log(
                $"MaxPositions={MaxPositions}, universeSize={_universeSize}, excludeETFs={_excludeETFs}, atrThreshold={_atrThreshold}, " +
                $"indicatorPeriod={_indicatorPeriod}, openingRangeMinutes={_openingRangeMinutes}, entryBarMinutes={_entryBarMinutes}, stopLossAtrDistance={stopLossAtrDistance}, " +
                $"stopLossRiskSize={stopLossRiskSize}, reversing={reversing}, maximisePositions={_maximisePositions}, " + 
                $"secondsResolution={_secondsResolution}, doubling={_doubling}, fees={_fees}"
            );
        }
        
        private void ResetVars()
        {
            _maxLongPositions = 0;
            _maxShortPositions = 0;
            _maxPositions = 0;
            _maxMarginUsed = 0.0m;
        }

        private void UpdatePlots()
        {
            Plot("Positions", "Long", _maxLongPositions);
            Plot("Positions", "Short", _maxShortPositions);
            Plot("Positions", "Total", _maxPositions);
            Plot("Margin", "Used", _maxMarginUsed);
        }

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            // Add indicators for each asset that enters the universe.
            foreach (var security in changes.AddedSecurities)
            {
                _symbolDataBySymbol[security.Symbol] = new SymbolData(this, security,
                    _openingRangeMinutes, _indicatorPeriod, _entryBarMinutes);
            }
        }

        public override void OnData(Slice slice)
        {
            int LongPositions = 0, ShortPositions = 0;
            foreach (var kvp in Portfolio) 
            { 
                if (kvp.Value.Quantity > 0) LongPositions += 1; 
                if (kvp.Value.Quantity < 0) ShortPositions += 1; 
            }

            _maxLongPositions = Math.Max(_maxLongPositions, LongPositions);
            _maxShortPositions = Math.Max(_maxShortPositions, ShortPositions);
            _maxPositions = Math.Max(_maxPositions, LongPositions + ShortPositions);
            _maxMarginUsed = Math.Max(_maxMarginUsed, Portfolio.TotalMarginUsed / Portfolio.TotalPortfolioValue);

            if (IsWarmingUp) return;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled) return;
            _symbolDataBySymbol[orderEvent.Symbol].OnOrderEvent(orderEvent);
        }

        public void CheckToCancelRemainingEntries()
        {
            if (_maximisePositions == 0) return;

            int openPositionsCount = 0;
            foreach (var kvp in Portfolio) { if (kvp.Value.Invested) openPositionsCount += 1; }
            if (openPositionsCount >= MaxPositions) {
                foreach (var symbolData in _symbolDataBySymbol.Values)
                {
                    if (symbolData.EntryTicket != null && symbolData.EntryTicket.Status == OrderStatus.Submitted)
                    {
                        symbolData.EntryTicket.Cancel();
                        symbolData.ClearEntryTicket();
                    }
                }
            }
        }
    }

    class SymbolData 
    {
        public decimal? RelativeVolume;
        public TradeBar OpeningBar = new();
        private OpeningRangeBreakoutUniverseAlgorithm _algorithm;
        private Security _security;
        private IDataConsolidator _openingRangeConsolidator;
        private IDataConsolidator _entryConsolidator;
        public decimal Upper { get; private set; }
        public decimal Lower { get; private set; }
        public AverageTrueRange ATR;
        private SimpleMovingAverage VolumeSMA;
        private decimal EntryPrice;
        private int Quantity;
        public OrderTicket EntryTicket, StopLossTicket;
        public bool Reversed = false;

        public SymbolData(OpeningRangeBreakoutUniverseAlgorithm algorithm,
            Security security,
            int openingRangeMinutes,
            int indicatorPeriod,
            int entryBarMinutes)
        {
            _algorithm = algorithm;
            _security = security;
            _openingRangeConsolidator = algorithm.Consolidate(security.Symbol,
                TimeSpan.FromMinutes(openingRangeMinutes), OpeningRangeHandler);
            _entryConsolidator = algorithm.Consolidate(security.Symbol,
                TimeSpan.FromMinutes(entryBarMinutes), EntryHandler);
            ATR = algorithm.ATR(security.Symbol, indicatorPeriod, resolution: Resolution.Daily);
            VolumeSMA = new SimpleMovingAverage(indicatorPeriod);
        }

        private void OpeningRangeHandler(TradeBar bar)
        {
            if (OpeningBar.Time.Date == bar.Time.Date) return;
            RelativeVolume = VolumeSMA.IsReady && VolumeSMA > 0 ? bar.Volume / VolumeSMA : null;
            VolumeSMA.Update(bar.EndTime, bar.Volume);
            OpeningBar = bar;
            Upper = bar.High;
            Lower = bar.Low;
        }

        private void EntryHandler(TradeBar bar)
        {
            if (OpeningBar.Time.Date != bar.Time.Date) return;
            if (_algorithm.Portfolio[_security.Symbol].Invested || EntryTicket != null)
                return;

            if (bar.Close > Upper)
            {
                PlaceTrade(bar.Close, bar.Close - _algorithm.stopLossAtrDistance * ATR.Current.Value);
                Reversed = false;
            }
            else if (bar.Close < Lower)
            {
                PlaceTrade(bar.Close, bar.Close + _algorithm.stopLossAtrDistance * ATR.Current.Value);
                Reversed = false;
            }
        }


        public void PlaceTrade(decimal entryPrice, decimal stopPrice)
        {
            var quantity = (int)((_algorithm.stopLossRiskSize * _algorithm.Portfolio.TotalPortfolioValue / _algorithm.MaxPositions) / (entryPrice - stopPrice));
            var quantityLimit = _algorithm.CalculateOrderQuantity(_security.Symbol, 1m/_algorithm.MaxPositions);
            quantity = (int)(Math.Min(Math.Abs(quantity), quantityLimit) * Math.Sign(quantity));
            if (quantity != 0)
            {
                EntryPrice = entryPrice;
                Quantity = quantity;
                EntryTicket = _algorithm.MarketOrder(_security.Symbol, quantity, false, tag: "Entry");
            }
        }

        public void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.OrderId == EntryTicket?.OrderId)
            {
                var trailingDistance = _algorithm.stopLossAtrDistance * ATR.Current.Value;
                StopLossTicket = _algorithm.TrailingStopOrder(
                    _security.Symbol,
                    -Quantity,
                    trailingDistance,
                    false,
                    tag: "Trailing Stop"
                );
            }

            if (orderEvent.OrderId == StopLossTicket?.OrderId && _algorithm.reversing == 1 && !Reversed)
            {
                _algorithm.MarketOrder(_security.Symbol, -Quantity, tag: "Reversed");
                StopLossTicket = _algorithm.StopMarketOrder(_security.Symbol, Quantity, EntryPrice, tag: "Reversed ATR Stop");
                Reversed = true;
            }
        }

        public void ClearEntryTicket()
        {
            EntryTicket = null;
        }
    }
}