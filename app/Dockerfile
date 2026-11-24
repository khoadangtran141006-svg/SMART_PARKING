
FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONPATH=/workspace \
    FLASK_DEBUG=0

RUN apt-get update && apt-get install -y --no-install-recommends \
    libglib2.0-0 \
    libgomp1 \
    tesseract-ocr \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY requirements-runtime.txt ./requirements-runtime.txt

RUN python -m pip install --no-cache-dir --upgrade pip \
    && python -m pip install --no-cache-dir -r requirements-runtime.txt

COPY app/ ./app/
COPY src/ ./src/
COPY models/classification/ ./models/classification/
COPY models/vision/best.onnx ./models/vision/best.onnx
COPY outputs/results/ ./outputs/results/

RUN groupadd --gid 1000 app \
    && useradd --uid 1000 --gid app --no-create-home --home-dir /workspace app \
    && mkdir -p /workspace/data/processed/scans \
    && chown -R app:app /workspace

ENV SMARTPARKING_API_URL=https://smartparking-backend-zv2v.onrender.com

EXPOSE 5000

USER app

CMD ["gunicorn", "--bind=0.0.0.0:5000", "--workers=1", "--threads=4", "--timeout=120", "--access-logfile=-", "--error-logfile=-", "app.app:app"]
