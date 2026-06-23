import requests
import time

url = "http://localhost:8082/v1/chat/completions"

payload = {
    "messages": [
        {
            "role": "system",
            "content": "Return ONLY JSON."
        },
        {
            "role": "user",
            "content": "Create Models/TestFeature.cs as JSON patch operations."
        }
    ],
    "temperature": 0,
    "stream": False
}

start = time.time()

response = requests.post(
    url,
    json=payload,
    timeout=300
)

elapsed = time.time() - start

print("TIME:", round(elapsed,2), "seconds")
print()
print(response.text)
