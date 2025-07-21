from AlgorithmImports import *                        # pull all QuantConnect names into scope
from datetime import timedelta
from typing import Dict, Set, Optional, Tuple as TypingTuple

# Todo add resets to the alpha                         # DEVELOPMENT-NOTE: daily reset still pending

class OpeningRangeBreakoutAlphaModel(AlphaModel):
    """
    For each security:
      • Records the first N-minute "opening range" (low, high).
      • Emits a 1-minute Price Insight when price breaks above or below that range.
    """

    def __init__(self, algorithm: QCAlgorithm, range_minutes: int = 15):
        self.algorithm = algorithm                      # keep handle to the parent algorithm
        self.range_period = timedelta(minutes=range_minutes)  # width of opening window
        # map: Symbol → (low, high); values start as (None, None)
        self.opening_range: Dict[Symbol, TypingTuple[Optional[float], Optional[float]]] = {}
        self.signaled: Set[Symbol] = set()              # tracks symbols that already fired today
        # IMPROVE: consider storing timestamps alongside (low, high) to guard against stale data

    # ----------------------------------------------------------------------
    #  Universe change hook
    # ----------------------------------------------------------------------
    def OnSecuritiesChanged(self, algorithm: QCAlgorithm, changes: SecurityChanges):
        for added in changes.added_securities:
            self.opening_range[added.Symbol] = (None, None)  # initialise range container
            self.signaled.discard(added.Symbol)              # allow fresh signal for new symbol
        for removed in changes.removed_securities:
            self.opening_range.pop(removed.Symbol, None)     # drop state for removed symbol
            self.signaled.discard(removed.Symbol)

    # ----------------------------------------------------------------------
    #  Per-slice update: emit Insights
    # ----------------------------------------------------------------------
    def Update(self, algorithm: QCAlgorithm, data: Slice) -> List[Insight]:
        insights: List[Insight] = []
        for symbol, (low, high) in self.opening_range.items():
            # skip if the opening range not yet computed or signal already fired
            if low is None or high is None or symbol in self.signaled:
                continue

            bar = data.bars.get_value(symbol)                # current minute bar
            if bar is None:
                continue

            direction = None
            if bar.close > high:
                direction = InsightDirection.UP
            elif bar.close < low:
                direction = InsightDirection.DOWN

            if direction:
                insights.append(Insight.price(symbol, self.range_period, direction))
                self.signaled.add(symbol)                    # prevent duplicate signals
                # STRATEGY: could emit a Weight or Confidence proportional to breakout distance

        return insights

    # ----------------------------------------------------------------------
    #  Helper scheduled function: compute opening range
    # ----------------------------------------------------------------------
    def RecordOpeningRange(self):
        """
        Should be scheduled range_minutes after market open.
        Pulls minute history for tracked symbols and saves (low, high).
        """
        history = self.algorithm.history(                    # request N-minute window
            list(self.opening_range.keys()),
            self.range_period,
            Resolution.MINUTE
        )
        if history.empty:
            return                                           # nothing to compute

        # group by symbol because the DataFrame index is (Symbol, Time)
        for symbol, df in history.groupby(level=0):
            low = df['low'].min()
            high = df['high'].max()
            self.opening_range[symbol] = (low, high)
            # IMPROVE: validate that df covers the full expected period; otherwise delay recording
            # STRATEGY: capture mid-range (high+low)/2 as a neutrality level for scaled position sizing
