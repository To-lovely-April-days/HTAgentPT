# 助手的代码解释器：Python + 常用计算/作图库 + 化工计算库 + 做文件的库。国内镜像源，离线机器可改 PY_IMAGE / PIP_INDEX。
# 化工计算库都是 MIT 许可（可商用）：CoolProp（物性）、fluids（管道、调节阀、安全阀 API 520）、ht（换热）、chemicals / thermo（化学品数据和物性）
ARG PY_IMAGE=docker.m.daocloud.io/library/python:3.12-slim
FROM ${PY_IMAGE}
ARG PIP_INDEX=https://pypi.tuna.tsinghua.edu.cn/simple

# 中文字体（图里的中文不乱码）
RUN sed -i 's/deb.debian.org/mirrors.aliyun.com/g' /etc/apt/sources.list.d/debian.sources 2>/dev/null || true \
 && apt-get update && apt-get install -y --no-install-recommends fonts-wqy-microhei \
 && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir -i ${PIP_INDEX} numpy scipy pandas matplotlib CoolProp sympy \
 && pip install --no-cache-dir -i ${PIP_INDEX} fluids==1.3.1 ht==1.2.0 chemicals==1.5.2 thermo==0.6.1 \
 && python -c "import fluids, ht, chemicals, thermo, CoolProp; print('calc libs ok')"

# 做文件：Excel、Word、PPT、PDF（都是开源库：openpyxl MIT、python-docx MIT、python-pptx MIT、pypdf BSD、reportlab BSD）
RUN pip install --no-cache-dir -i ${PIP_INDEX} openpyxl==3.1.5 python-docx==1.2.0 python-pptx==1.0.2 pypdf==5.4.0 reportlab==4.4.1 \
 && python -c "import openpyxl, docx, pptx, pypdf, reportlab; print('file libs ok')"
# Word 修订 / 批注、PDF 水印的小工具，装进 site-packages（沙箱用 python -I 跑，不认 PYTHONPATH）
COPY htfiles.py /tmp/htfiles.py
RUN cp /tmp/htfiles.py "$(python -c 'import site; print(site.getsitepackages()[0])')/htfiles.py" && python -c "import htfiles"

RUN useradd -m -u 10001 runner
COPY server.py /srv/server.py
USER runner
WORKDIR /tmp
EXPOSE 8000
CMD ["python", "/srv/server.py"]
