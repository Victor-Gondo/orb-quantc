Opening Range Breakout Universe Algorithm

This repository contains a QuantConnect algorithm written in C# designed to implement an opening-range breakout strategy across a universe of selected equities.

Strategy Overview

The algorithm identifies equities experiencing high relative volume in the early minutes after market open, then trades breakouts beyond the established opening range.

Key Features

Opening Range Calculation: Establishes an opening-range based on customizable minutes at market open.

ATR-based Position Sizing: Dynamically calculates trade sizes based on volatility (Average True Range).

Trailing Stops & Reversals: Manages positions with ATR-based trailing stops and supports reversal trades.

Universe Selection: Filters equities based on liquidity, relative volume, and configurable universe size.

Parameters

Customize the algorithm through these parameters:

MaxPositions: Maximum simultaneous open positions.

universeSize: Number of equities to select.

excludeETFs: Toggle ETF inclusion.

atrThreshold: Minimum ATR threshold for volatility filtering.

indicatorPeriod: Period for indicators (ATR, volume SMA).

openingRangeMinutes: Minutes after open defining the opening range.

entryBarMinutes: Bar interval in minutes for trade entry confirmation.

stopLossAtrDistance: Stop loss distance as a fraction of ATR.

stopLossRiskSize: Max portfolio risk per trade (percentage).

reversing: Enables reversing trades after stops.

fees: Enables or disables simulated broker fees.

Implementation Details

Written for the QuantConnect algorithmic trading platform.

Utilizes consolidators for opening-range and entry-bar management.

Leverages QuantConnect’s built-in ATR and SMA indicators.

Usage

Deploy to QuantConnect by uploading main.cs or by copying the algorithm directly into QuantConnect's IDE.

Requirements

QuantConnect account (https://www.quantconnect.com/)

C# (.NET)

License

This project is provided as-is without warranty under the MIT License.

