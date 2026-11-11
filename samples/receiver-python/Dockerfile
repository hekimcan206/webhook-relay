# Standard library only: nothing to pip install.
FROM python:3.12-slim
WORKDIR /app
COPY standard_webhooks.py receiver.py ./
RUN useradd --system --uid 10001 receiver
USER 10001
ENV PORT=8080 PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
EXPOSE 8080
CMD ["python", "receiver.py"]
