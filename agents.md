### Codex Agents

This document describes available Codex agents for algorithm development and testing.

### `backtest-agent`

* **Purpose**: Automates running backtests on specified date ranges and parameter sets.
* **Usage**:

  ```bash
  codex agent run backtest-agent --start 2024-01-01 --end 2025-01-01
  ```

### `optimize-agent`

* **Purpose**: Performs parameter sweeps for volume, market cap, and breakout thresholds.
* **Usage**:

  ```bash
  codex agent run optimize-agent --param volume --values 100k,500k,1M
  ```

### `report-agent`

* **Purpose**: Generates PDF performance reports with charts and metrics.
* **Usage**:

  ```bash
  codex agent run report-agent --output reports/performance.pdf
  ```

### Developing New Agents

Agents live in the `agents/` directory. Each agent has a YAML manifest and Python handler:

```yaml
# example agents/hello-agent.yaml
name: hello-agent
description: Responds with a greeting
entrypoint: hello.py
```

Implement the logic in `agents/hello.py`. Refer to Codex developer docs for API details.
