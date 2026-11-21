# 助手的内置浏览器：Chromium（Debian 软件源）+ Playwright（Apache-2.0）。国内镜像源，离线机器可改 PY_IMAGE / PIP_INDEX。
ARG PY_IMAGE=docker.m.daocloud.io/library/python:3.12-slim
FROM ${PY_IMAGE}
ARG PIP_INDEX=https://pypi.tuna.tsinghua.edu.cn/simple

RUN sed -i 's/deb.debian.org/mirrors.aliyun.com/g' /etc/apt/sources.list.d/debian.sources 2>/dev/null || true \
 && apt-get update && apt-get install -y --no-install-recommends chromium fonts-wqy-microhei fonts-noto-cjk \
 && rm -rf /var/lib/apt/lists/*

# 用系统的 Chromium，不再下载 Playwright 自带的浏览器（国内下载慢）
RUN pip install --no-cache-dir -i ${PIP_INDEX} playwright==1.48.0 \
 && python -c "import playwright; print('playwright ok')"

RUN useradd -m -u 10001 browser
COPY server.py /srv/server.py
USER browser
WORKDIR /home/browser
ENV CHROMIUM_PATH=/usr/bin/chromium PORT=8100
EXPOSE 8100
CMD ["python", "/srv/server.py"]
