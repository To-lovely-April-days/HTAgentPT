# Word → PDF 转换、检查助手做的文件、PPT 每页转图：LibreOffice 无界面模式（文字、表格、演示三个组件）+ poppler（PDF 转图）。
# PPT 模板常用的开源英文字体（Lato、Open Sans、Roboto、Montserrat、Inter……）装上，转出来的图更接近原样；源里没有的跳过。国内镜像源，离线机器可改 PY_IMAGE / PIP_INDEX。
ARG PY_IMAGE=docker.m.daocloud.io/library/python:3.12-slim
FROM ${PY_IMAGE}
ARG PIP_INDEX=https://pypi.tuna.tsinghua.edu.cn/simple

RUN sed -i 's/deb.debian.org/mirrors.aliyun.com/g' /etc/apt/sources.list.d/debian.sources 2>/dev/null || true \
 && apt-get update && apt-get install -y --no-install-recommends \
      libreoffice-writer-nogui libreoffice-calc-nogui libreoffice-impress-nogui fonts-wqy-microhei fonts-wqy-zenhei fonts-noto-cjk \
      poppler-utils \
 && for f in fonts-lato fonts-open-sans fonts-roboto fonts-montserrat fonts-inter fonts-firacode fonts-oswald; do \
      apt-get install -y --no-install-recommends "$f" || echo "skip $f"; done \
 && rm -rf /var/lib/apt/lists/*

# 检查文件：openpyxl 读公式和重算结果、pypdf 数页数
RUN pip install --no-cache-dir -i ${PIP_INDEX} openpyxl==3.1.5 pypdf==5.4.0

RUN useradd -m -u 10001 office
COPY server.py /srv/server.py
USER office
WORKDIR /tmp
EXPOSE 3000
CMD ["python", "/srv/server.py"]
