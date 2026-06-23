#!/bin/bash
set -e

source .venv/bin/activate

for suite in \
  benchmark_cases_normal.json \
  benchmark_cases_ui.json \
  benchmark_cases_hallucination.json \
  benchmark_cases_devportal.json \
  benchmark_cases_context.json \
  benchmark_cases_patchbuilder_reliability.json
do
    echo
    echo "======================================"
    echo "Running $suite"
    echo "======================================"

    cp "$suite" benchmark_cases.json
    rm -f results/*.json
    python benchmark.py

    mkdir -p "results-$(basename "$suite" .json)"
    cp results/*.json "results-$(basename "$suite" .json)/"
done

echo "Finished"
