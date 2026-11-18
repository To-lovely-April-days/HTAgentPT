# Word → PDF 转换：LibreOffice 无界面模式。国内镜像源，离线机器可改 PY_IMAGE。
ARG PY_IMAGE=docker.m.daocloud.io/library/python:3.12-slim
FROM ${PY_IMAGE}

RUN sed -i 's/deb.debian.org/mirrors.aliyun.com/g' /etc/apt/sources.list.d/debian.sources 2>/dev/null || true \
 && apt-get update && apt-get install -y --no-install-recommends \
      libreoffice-writer-nogui fonts-wqy-microhei fonts-wqy-zenhei fonts-noto-cjk \
 && rm -rf /var/lib/apt/lists/*

RUN useradd -m -u 10001 office
COPY server.py /srv/server.py
USER office
WORKDIR /tmp
EXPOSE 3000
CMD ["python", "/srv/server.py"]
