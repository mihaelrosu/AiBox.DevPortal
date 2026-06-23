import json
import time
from pathlib import Path
import requests

MODELS = [
    {
        "name": "llamacpp-qwen25-coder-7b",
        "url": "http://localhost:8082/v1/chat/completions",
        "model": "local"
    }
]

SYSTEM_PROMPT = """
Return ONLY this JSON shape. No markdown. No explanation.

{
  "operations": [
    {
      "filePath": "Models/TestFeature.cs",
      "operation": "create",
      "oldText": "",
      "newText": "namespace AiBox.DevPortal.Models;\\n\\npublic sealed class TestFeature\\n{\\n    public string Name { get; set; } = string.Empty;\\n}\\n",
      "summary": "Create test feature model"
    }
  ]
}
"""

def extract_json(content):
    text = content.strip()

    if text.startswith("```"):
        text = text.replace("```json", "").replace("```", "").strip()

    start = text.find("{")
    end = text.rfind("}")

    if start >= 0 and end > start:
        return text[start:end + 1]

    return text

def call_model(route, task):
    started = time.time()

    response = requests.post(
        route["url"],
        json={
            "model": route["model"],
            "messages": [
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": task}
            ],
            "temperature": 0,
            "stream": False
        },
        timeout=300
    )

    elapsed = round(time.time() - started, 2)
    response.raise_for_status()

    body = response.json()
    content = body["choices"][0]["message"]["content"]

    return content, elapsed

def score_response(content, expected):
    score = 0
    errors = []

    json_text = extract_json(content)

    try:
        data = json.loads(json_text)
        score += 50
    except Exception as ex:
        return 0, [f"Invalid JSON: {ex}"]

    if isinstance(data, list):
        data = {
            "operations": data
        }

    operations = data.get("operations", [])

    if isinstance(operations, list):
        score += 20
    else:
        errors.append("Missing operations list")
        operations = []

    files = [x.get("filePath") for x in operations if isinstance(x, dict)]

    if "mustContainFile" in expected:
        if expected["mustContainFile"] in files:
            score += 20
        else:
            errors.append("Expected file missing")

    if len(operations) <= expected.get("maxOperations", 999):
        score += 10
    else:
        errors.append("Too many operations")

    if expected.get("mustRefusePatch"):
        if len(operations) == 0:
            score += 30
        else:
            errors.append("Model created patch when it should refuse")

    required_texts = expected.get("mustContainText", [])
    forbidden_texts = expected.get("mustNotContainText", [])
    all_new_text = "\n".join(
        x.get("newText", "")
        for x in operations
        if isinstance(x, dict)
    )

    if required_texts:
        searchable = all_new_text + "\n" + content
        missing = [
            item for item in required_texts
            if item not in searchable
        ]

        if not missing:
            score += 30
        else:
            errors.append("Missing required text: " + ", ".join(missing))

    if forbidden_texts:
        searchable = all_new_text + "\n" + content
        found = [
            item for item in forbidden_texts
            if item in searchable
        ]

        if found:
            score = 0
            errors.append("Forbidden hallucinated text: " + ", ".join(found))

    return min(score, 100), errors

cases = json.loads(Path("benchmark_cases.json").read_text())
Path("results").mkdir(exist_ok=True)

for route in MODELS:
    print(f"\nBenchmarking {route['name']}")
    results = []

    for case in cases:
        try:
            content, elapsed = call_model(route, case["task"])
            score, errors = score_response(content, case["expected"])

            print(f"  {case['id']}: score={score} time={elapsed}s")

            results.append({
                "caseId": case["id"],
                "score": score,
                "elapsedSeconds": elapsed,
                "errors": errors,
                "raw": content[:2000]
            })

        except Exception as ex:
            results.append({
                "caseId": case["id"],
                "score": 0,
                "elapsedSeconds": 0,
                "errors": [str(ex)],
                "raw": ""
            })

    avg = sum(r["score"] for r in results) / len(results)

    output = {
        "model": route["name"],
        "averageScore": avg,
        "results": results
    }

    Path(f"results/{route['name']}.json").write_text(
        json.dumps(output, indent=2)
    )

    print(f"Average score: {avg:.2f}")

print("\nDone.")
