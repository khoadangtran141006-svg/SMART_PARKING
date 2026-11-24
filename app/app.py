from datetime import datetime
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

import base64
import io
import json
import cv2
import numpy as np
import os
import ssl
import sys

import cloudinary
import cloudinary.uploader

from flask import Flask, jsonify, redirect, request, send_from_directory

BASE_DIR = Path(__file__).resolve().parent
ROOT_DIR = BASE_DIR.parent

if str(ROOT_DIR) not in sys.path:
    sys.path.insert(0, str(ROOT_DIR))

from src.models.classification import (
    load_color_model,
    load_knn_model,
    predict_plate_color,
)
from src.vision.plate_ocr import normalize_plate, recognize_plate


app = Flask(__name__)
app.config["MAX_CONTENT_LENGTH"] = 8 * 1024 * 1024
CORS_ALLOWED_ORIGINS = {
    origin.strip().rstrip("/")
    for origin in os.getenv("CORS_ALLOWED_ORIGINS", "").split(",")
    if origin.strip()
}


@app.after_request
def add_cors_headers(response):
    origin = request.headers.get("Origin", "").rstrip("/")
    if origin and origin in CORS_ALLOWED_ORIGINS:
        response.headers["Access-Control-Allow-Origin"] = origin
        response.headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
        response.headers["Access-Control-Allow-Headers"] = "Accept, Content-Type"
        vary = response.headers.get("Vary", "")
        if "Origin" not in {value.strip() for value in vary.split(",")}:
            response.headers["Vary"] = f"{vary}, Origin".strip(", ")
    return response


@app.get("/health")
def health_check():
    return jsonify({"status": "ok"})


SCAN_DIR = ROOT_DIR / "data" / "processed" / "scans"
SCAN_DIR.mkdir(parents=True, exist_ok=True)

CLOUDINARY_CLOUD_NAME = os.getenv("CLOUDINARY_CLOUD_NAME")
CLOUDINARY_API_KEY = os.getenv("CLOUDINARY_API_KEY")
CLOUDINARY_API_SECRET = os.getenv("CLOUDINARY_API_SECRET")

cloudinary.config(
    cloud_name=CLOUDINARY_CLOUD_NAME,
    api_key=CLOUDINARY_API_KEY,
    api_secret=CLOUDINARY_API_SECRET,
    secure=True,
)

COLOR_MODEL_META_PATH = ROOT_DIR / "models" / "classification" / "model_metadata.json"
ALGORITHM_COMPARISON_PATH = ROOT_DIR / "outputs" / "results" / "algorithm_comparison.json"
color_model = load_color_model()
knn_color_model = load_knn_model()

SMARTPARKING_API_URL = os.getenv(
    "SMARTPARKING_API_URL",
    "http://127.0.0.1:5049",
).rstrip("/")


class BackendApiError(Exception):
    def __init__(self, status_code: int, message: str, body=None):
        super().__init__(message)
        self.status_code = status_code
        self.message = message
        self.body = body


def _backend_ssl_context():
    if SMARTPARKING_API_URL.startswith(
        ("https://localhost", "https://127.0.0.1")
    ):
        return ssl._create_unverified_context()
    return None


def _http_json_request(method: str, url: str, payload=None):
    headers = {"Accept": "application/json"}
    body = None

    if payload is not None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = Request(
        url,
        data=body,
        headers=headers,
        method=method.upper(),
    )

    try:
        context = _backend_ssl_context()
        if context is not None:
            response = urlopen(req, timeout=20, context=context)
        else:
            response = urlopen(req, timeout=20)

        with response:
            raw = response.read().decode("utf-8")

        if not raw:
            return {}

        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            return {"raw": raw}

    except HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            body_data = json.loads(raw)
        except json.JSONDecodeError:
            body_data = {"message": raw}

        message = (
            body_data.get("message")
            or body_data.get("title")
            or "Backend trả HTTP {}.".format(exc.code)
        )
        raise BackendApiError(exc.code, message, body_data) from exc

    except URLError as exc:
        raise BackendApiError(
            0,
            (
                "Không thể kết nối SmartParking Backend. "
                "Địa chỉ hiện tại: {}. Chi tiết: {}".format(
                    SMARTPARKING_API_URL,
                    exc.reason,
                )
            ),
        ) from exc


def _backend_request(method: str, path: str, payload=None):
    return _http_json_request(
        method,
        "{}{}".format(SMARTPARKING_API_URL, path),
        payload,
    )


def _backend_error_response(exc: BackendApiError):
    status_code = exc.status_code
    if status_code < 400 or status_code > 599:
        status_code = 503

    return jsonify(
        {
            "status": "error",
            "message": exc.message,
            "backend": SMARTPARKING_API_URL,
        }
    ), status_code


def _confidence_for_backend(ocr: dict) -> float:
    confidence = ocr.get("confidence")
    return float(1.0 if confidence is None else confidence)


_scan_history = []


def _save_scan_record(record: dict):
    _scan_history.insert(0, record)
    if len(_scan_history) > 100:
        del _scan_history[100:]


def _classify_crop_b64(crop_b64: str | None) -> dict:
    if not crop_b64:
        return {}
    try:
        raw = base64.b64decode(crop_b64, validate=True)
        arr = np.frombuffer(raw, dtype=np.uint8)
        crop_bgr = cv2.imdecode(arr, cv2.IMREAD_COLOR)
        if crop_bgr is None:
            return {}
        return predict_plate_color(
            color_model,
            crop_bgr,
            comparison_model=knn_color_model,
        )
    except Exception as exc:
        print("[Warning] Không thể phân loại màu biển số: {}".format(exc))
        return {}


def _safe_upload_name(filename: str):
    name = Path(filename).name
    safe = "".join(
        char if char.isalnum() or char in ".-_" else "_"
        for char in name
    )
    return safe[:80] or "plate-image.jpg"

def _upload_image_to_cloudinary(
    image_bytes: bytes,
    public_id: str,
) -> str:
    if not image_bytes:
        raise ValueError("Không có dữ liệu ảnh để upload.")

    if not all([
        CLOUDINARY_CLOUD_NAME,
        CLOUDINARY_API_KEY,
        CLOUDINARY_API_SECRET,
    ]):
        raise RuntimeError(
            "Thiếu cấu hình Cloudinary. "
            "Vui lòng kiểm tra CLOUDINARY_CLOUD_NAME, "
            "CLOUDINARY_API_KEY và CLOUDINARY_API_SECRET."
        )

    result = cloudinary.uploader.upload(
        io.BytesIO(image_bytes),
        folder="smart-parking/scans",
        public_id=public_id,
        resource_type="image",
        overwrite=False,
    )

    image_url = result.get("secure_url")

    if not image_url:
        raise RuntimeError("Cloudinary không trả về secure_url.")

    return image_url

@app.route("/")
@app.route("/dashboard")
@app.route("/history")
def frontend_redirect():
    return redirect("http://localhost:5173/", code=302)


@app.route("/api/model-info")
def model_info():
    if COLOR_MODEL_META_PATH.is_file():
        try:
            data = json.loads(COLOR_MODEL_META_PATH.read_text(encoding="utf-8"))
            return jsonify({"status": "success", "model": data})
        except (OSError, json.JSONDecodeError) as exc:
            return jsonify({"status": "error", "message": str(exc)}), 500
    return jsonify({
        "status": "error",
        "message": "Chưa có metadata mô hình. Hãy chạy: python train_plate_color.py",
    }), 404


@app.route("/api/classify-plate-color", methods=["POST"])
def classify_plate_color_only():
    uploaded = request.files.get("image")
    if not uploaded or not uploaded.filename:
        return jsonify({"status": "error", "message": "Hãy gửi kèm file ảnh vùng biển số."}), 400
    image_bytes = uploaded.read()
    if not image_bytes:
        return jsonify({"status": "error", "message": "File ảnh không có dữ liệu."}), 400
    image = cv2.imdecode(np.frombuffer(image_bytes, dtype=np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        return jsonify({"status": "error", "message": "Không giải mã được file ảnh."}), 400
    try:
        return jsonify({
            "status": "success",
            **predict_plate_color(
                color_model,
                image,
                comparison_model=knn_color_model,
            ),
        })
    except Exception as exc:
        return jsonify({"status": "error", "message": str(exc)}), 500


def _read_algorithm_comparison():
    if not ALGORITHM_COMPARISON_PATH.is_file():
        return None
    try:
        return json.loads(
            ALGORITHM_COMPARISON_PATH.read_text(encoding="utf-8")
        )
    except (OSError, json.JSONDecodeError) as exc:
        print("[Warning] Không đọc được báo cáo so sánh mô hình: {}".format(exc))
        return None


@app.route("/api/scan-plate", methods=["POST"])
def scan_plate():
    try:
        uploaded = request.files.get("image")
        if not uploaded or not uploaded.filename:
            return jsonify(
                {
                    "status": "error",
                    "message": "Hãy chọn một ảnh có biển số xe.",
                }
            ), 400

        image_bytes = uploaded.read()
        if not image_bytes:
            return jsonify(
                {
                    "status": "error",
                    "message": "File ảnh không có dữ liệu.",
                }
            ), 400

        ocr = recognize_plate(image_bytes)
        detected_plate = normalize_plate(ocr.get("plate") or "")
        plate_color = _classify_crop_b64(ocr.get("crop_jpeg_b64"))
        color_label = plate_color.get("color_label") or "Chưa phân loại màu biển số"

        
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        safe_filename = _safe_upload_name(uploaded.filename)

        # Upload ảnh gốc lên Cloudinary
        original_public_id = "{}_{}".format(
            stamp,
            Path(safe_filename).stem,
        )

        image_url = _upload_image_to_cloudinary(
            image_bytes,
            original_public_id,
        )

        # Upload ảnh biển số đã cắt nếu OCR tạo được crop
        crop_url = None
        crop_base64 = ocr.get("crop_jpeg_b64")

        if crop_base64:
            crop_bytes = base64.b64decode(crop_base64)

            crop_url = _upload_image_to_cloudinary(
                crop_bytes,
                "{}_crop".format(stamp),
            )

        # Lưu thông tin nhận diện cùng URL Cloudinary
        record = {
            "id": stamp,
            "plate": detected_plate,
            "valid": ocr.get("valid"),
            "confidence": ocr.get("confidence"),
            "engine": ocr.get("engine"),
            "source_filename": uploaded.filename,
            "image_path": image_url,
            "crop_path": crop_url,
            "plate_color": plate_color,
            "extra": {
                "bbox": ocr.get("bbox"),
                "raw_candidates": ocr.get("raw_candidates"),
                "message": ocr.get("message"),
            },
        }

        _save_scan_record(record)


        database_saved = False
        database_message = None

        if detected_plate:
            try:
                _backend_request(
                    "POST",
                    "/api/PlateScans",
                    payload={
                        "plate": detected_plate,
                        "confidence": _confidence_for_backend(ocr),
                        "imagePath": image_url,
                        "cropImagePath": crop_url,                        
                        "plateColorCode": (
                            plate_color.get("color_code")
                            if plate_color.get("color_code") != "Unknown"
                            else None
                        ),
                        "plateColorLabel": (
                            plate_color.get("color_label")
                            if plate_color.get("color_code") != "Unknown"
                            else None
                        ),
                        "plateColorConfidence": (
                            plate_color.get("confidence")
                            if plate_color.get("color_code") != "Unknown"
                            else None
                        ),
                        "note": "{} | {}".format(ocr.get("engine") or "Plate OCR", color_label),
                    },
                )
                database_saved = True
            except BackendApiError as exc:
                database_message = exc.message
                print(
                    "[Warning] Không thể lưu lượt nhận diện vào PostgreSQL: {}".format(
                        exc.message
                    )
                )

        return jsonify(
            {
                "status": "success",
                "plate": detected_plate or None,
                "valid": ocr.get("valid"),
                "confidence": ocr.get("confidence"),
                "engine": ocr.get("engine"),
                "message": ocr.get("message"),
                "plate_color": plate_color,
                "algorithm_comparison": _read_algorithm_comparison(),
                "raw_candidates": ocr.get("raw_candidates", []),
                "bbox": ocr.get("bbox"),
                "crop_jpeg_b64": ocr.get("crop_jpeg_b64"),
                "annotated_jpeg_b64": ocr.get("annotated_jpeg_b64"),
                "saved": record,
                "image_path": image_url,
                "crop_path": crop_url,
                "database_saved": database_saved,
                "database_message": database_message,
            }
        )

    except BackendApiError as exc:
        return _backend_error_response(exc)

    except Exception as exc:
        return jsonify(
            {
                "status": "error",
                "message": str(exc),
            }
        ), 500


@app.route("/scan-image/<path:filename>")
def get_scan_image(filename):
    safe_name = Path(filename).name
    if not safe_name:
        return jsonify(
            {
                "status": "error",
                "message": "Tên ảnh không hợp lệ.",
            }
        ), 400

    image_path = SCAN_DIR / safe_name
    if not image_path.is_file():
        return jsonify(
            {
                "status": "error",
                "message": "Không tìm thấy ảnh nhận diện.",
            }
        ), 404

    return send_from_directory(SCAN_DIR, safe_name)


@app.route("/api/preview-plate", methods=["POST"])
def preview_plate():
    try:
        uploaded = request.files.get("image")
        if not uploaded or not uploaded.filename:
            return jsonify(
                {
                    "status": "error",
                    "message": "Không có ảnh camera.",
                }
            ), 400

        image_bytes = uploaded.read()
        if not image_bytes:
            return jsonify(
                {
                    "status": "error",
                    "message": "Frame camera không có dữ liệu.",
                }
            ), 400

        ocr = recognize_plate(image_bytes)
        plate = normalize_plate(ocr.get("plate") or "")
        plate_color = _classify_crop_b64(ocr.get("crop_jpeg_b64"))

        return jsonify(
            {
                "status": "success",
                "plate": plate,
                "valid": bool(ocr.get("valid")),
                "confidence": ocr.get("confidence", 0),
                "engine": ocr.get("engine", "none"),
                "message": ocr.get("message", ""),
                "plate_color": plate_color,
                "bbox": ocr.get("bbox"),
                "annotated_jpeg_b64": ocr.get("annotated_jpeg_b64"),
                "crop_jpeg_b64": ocr.get("crop_jpeg_b64"),
            }
        )

    except Exception as exc:
        return jsonify(
            {
                "status": "error",
                "message": str(exc),
            }
        ), 500


@app.route("/api/scans")
def list_scans():
    return jsonify(
        {
            "status": "success",
            "scans": _scan_history,
        }
    )


if __name__ == "__main__":
    print("==================================================")
    print(" ALPR System - Nhận diện biển số & phân loại màu")
    print(" YOLO/OCR + RandomForest Color Classifier + .NET API")
    print("==================================================")
    print("Backend: {}".format(SMARTPARKING_API_URL))
    print("Python: http://127.0.0.1:5000")
    print("==================================================")
    app.run(debug=True, port=5000)
