# main.py
# ---------- Imports ----------
from AlgorithmImports import *                               # QuantConnect convenience import
from universe import PremarketUniverse                      # custom universe selector
from alpha import OpeningRangeBreakoutAlphaModel            # custom alpha model
from QuantConnect.Algorithm.Framework.Risk import TrailingStopRiskManagementModel
from QuantConnect.Algorithm.Framework.Portfolio import RiskParityPortfolioConstructionModel
# TODO implement portfolio sizing as well as exit plan       # <-- already a helpful reminder

class PremarketBreakoutAlgorithm(QCAlgorithm):
    # --------------------------------------------
    #  INITIALISATION
    # --------------------------------------------
    def initialize(self):                                    # QC expects **Initialize** (Pascal-Case) Improve-Here
        # ------------ Back-test parameters ------------
        self.set_start_date(2024, 1, 1)                      # back-test begins Jan-01-2024
        self.set_end_date(2025, 1, 1)                        # ends Jan-01-2025
        self.set_cash(100_000)                               # initial portfolio equity USD 100 k
        self.set_brokerage_model(                            # use IB Cash account emulation
            BrokerageName.INTERACTIVE_BROKERS_BROKERAGE,
            AccountType.CASH
        )
        self.set_time_zone(TimeZones.NEW_YORK)               # run algorithm in NY exchange time
        # Improve-Here → QC method is SetTimeZone

        # ------------ Universe selection ------------
        self.universe_model = PremarketUniverse(             # build pre-market gap scanner
            algorithm=self,
            min_volume=500_000,                              # first-minute volume threshold
            min_market_cap=3e6,                              # market-cap cutoff (3 M) Strategy-Idea: raise to 3 B
            min_premarket_change=0.10,                       # ≥ 10 % gappers
            max_symbols=5                                    # keep universe small (≤5)
        )
        self.add_universe(                                   # hook custom coarse + fine filters
            self.universe_model.coarse_filter,
            self.universe_model.fine_filter
        )

        # ------------ Alpha model (signals) ------------
        self.alpha_model = OpeningRangeBreakoutAlphaModel(
            algorithm=self,
            range_minutes=30                                 # opening-range width = first 30 min
        )
        self.set_alpha(self.alpha_model)

        # ------------ Portfolio construction ------------
        # Daily-rebalanced risk-parity (volatility weighted)
        self.set_portfolio_construction(
            RiskParityPortfolioConstructionModel(
                lambda dt: dt + timedelta(days=1),           # next rebalance date = tomorrow
                resolution=Resolution.MINUTE
            )
        )
        # Strategy-Idea: tie rebalance to end-of-day only
        # Improve-Here: pass risk % (e.g. maximum_risk_per_symbol) when available

        # ------------ Scheduled tasks ------------
        # 1. Forced liquidation at 04:29 NY (pre-mkt close)
        self.schedule.on(
            self.date_rules.every_day(),
            self.time_rules.at(4, 29),                       # 04:29 NY 
            Action(self.liquidate)                           # exit all positions
        )
        # Strategy-Idea: move to 15:59 to stay intraday or remove if not using overnight
        # 2. Capture opening-range highs/lows at 09:40 NY
        self.schedule.on(
            self.date_rules.every_day(),
            self.time_rules.at(9, 40),
            self.alpha_model.RecordOpeningRange
        )
        # 3. Daily reset of state at 00:01 NY
        self.schedule.on(
            self.date_rules.every_day(),
            self.time_rules.at(0, 1),
            self.ResetDailyState
        )

    # --------------------------------------------
    #  HOUSE-KEEPING
    # --------------------------------------------
    def ResetDailyState(self):
        """
        Reset rolling data so the next session starts fresh.
        """
        # clear the list of already-signaled symbols
        self.alpha_model.signaled.clear()
        # reset stored (low, high) tuples
        self.alpha_model.opening_range = {
            symbol: (None, None) for symbol in self.alpha_model.opening_range
        }
        # un-freeze the pre-market universe
        self.universe_model._frozen = []
        self.Debug("Daily state reset")

    # --------------------------------------------
    #  ORDER MANAGEMENT
    # --------------------------------------------
    def OnOrderEvent(self, orderEvent: OrderEvent):
        """
        After an entry order fills, submit a take-profit limit for half
        the shares at +7 %.  The trailing-stop risk model handles the rest.
        """
        # ---- ignore non-fills or exits ----
        if orderEvent.Status != OrderStatus.FILLED or orderEvent.FillQuantity <= 0:
            return

        symbol   = orderEvent.Symbol
        holdings = self.Portfolio[symbol].Quantity           # current size (post-fill)
        entry_qty = orderEvent.FillQuantity                  # signed fill size
        take_profit_qty = abs(entry_qty) // 2                # half of filled quantity
        if take_profit_qty == 0:
            return

        take_profit_price = orderEvent.FillPrice * 1.07      # +7 % target
        direction = -math.copysign(1, entry_qty)             # flip sign: sell longs / buy-to-cover shorts
        self.LimitOrder(symbol,
                        int(direction * take_profit_qty),
                        take_profit_price)
        # Strategy-Idea: replace hard +7 % with ATR-based or VWAP band target
        # Improve-Here: consider using Tag to label orders for easier debugging
