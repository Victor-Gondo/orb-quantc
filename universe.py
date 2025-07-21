# universe.py
from AlgorithmImports import *          # QC helper import
from datetime import time

class PremarketUniverse:
    """
    Selects a small universe of opening-gap stocks.

    Parameters
    ----------
    min_volume : float
        Minimum **first regular-market minute** volume (shares).
    min_market_cap : float
        Minimum market cap filter (applied in fine selection).
    min_premarket_change : float
        % change vs. prior close required to qualify.
    max_symbols : int
        Maximum number of tickers to pass to the framework.
    """

    def __init__(
        self,
        algorithm: QCAlgorithm,
        min_volume: float = 500_000,
        min_market_cap: float = 3e6,    # Improve-Here: looks like 3 B was intended
        min_premarket_change: float = 0.10,
        max_symbols: int = 5
    ):
        self.algorithm = algorithm
        # ---------- Universe-level settings ----------
        # Enable minute resolution & extended hours so pre-market data exists
        algorithm.universe_settings.resolution = Resolution.MINUTE     # Improve-Here: Should be algorithm.UniverseSettings
        algorithm.universe_settings.extended_market_hours = True
        # Strategy-Idea: consider tick or second resolution for finer ORB

        # ---------- Thresholds ----------
        self.min_volume = min_volume
        self.min_market_cap = min_market_cap
        self.min_premarket_change = min_premarket_change
        self.max_symbols = max_symbols

        # ---------- Internal state ----------
        self._frozen: List[Symbol] = []  # list locked after 09:32 ET

    # ------------------------------------------------------------------
    #  COARSE FILTER  (executed before fine filter)
    # ------------------------------------------------------------------
    def coarse_filter(self, coarse: List[CoarseFundamental]) -> List[Symbol]:
        """
        Runs continuously until 09:32 NY.  Once the market opens and the
        universe is frozen, simply returns the stored list.
        """
        # stop updating two minutes after the opening print
        if self.algorithm.time.time() >= time(9, 32):        # Improve-Here: self.Time not algorithm.time
            return self._frozen

        candidates = []
        for cf in coarse:
            # ---------- Pull 1 bar of REGULAR-hours history ----------
            history = self.algorithm.history(                # Improve-Here: algorithm.History capital H
                cf.symbol,                                   # should be cf.Symbol
                1,
                Resolution.MINUTE,
                extended_market_hours=False                  # regular-hours only
            )
            if history.empty:
                continue

            first_bar = history.iloc[0]                      # first minute (09:30-09:31)
            # Safe access to volume
            if "volume" not in first_bar:
                # self.algorithm.Debug(f"Missing 'volume' field for {cf.Symbol}")
                continue
            first_vol = first_bar["volume"]
            if first_vol < self.min_volume:
                continue

            # ---------- Pre-market gap check ----------
            # CoarseFundamental fields are capitalised in QC
            change = (
                (cf.price - cf.adjusted_price) / cf.adjusted_price  # Improve-Here: use cf.Price & cf.AdjustedPrice
                if cf.adjusted_price != 0 else 0
            )
            if change < self.min_premarket_change:
                continue

            candidates.append((cf.symbol, change))           # tuple = (Symbol, %gap)

        # ---------- Sort & freeze ----------
        candidates.sort(key=lambda x: x[1], reverse=True)    # highest gappers first
        self._frozen = [sym for sym, _ in candidates[: self.max_symbols]]
        return self._frozen

    # ------------------------------------------------------------------
    #  FINE FILTER  (fundamental data)
    # ------------------------------------------------------------------
    def fine_filter(self, fine: List[FineFundamental]) -> List[Symbol]:
        """
        Keep only stocks whose CompanyProfile.MarketCap meets threshold.
        """
        selected = []
        for f in fine:
            profile = f.company_profile                       # fine fundamental object
            if profile is None or profile.MarketCap is None:
                continue
            if profile.MarketCap >= self.min_market_cap:
                selected.append(f.symbol)                     # Improve-Here: f.Symbol (Pascal-Case)
        return selected
