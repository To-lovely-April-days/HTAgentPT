# 助手的代码解释器：Python + 常用计算/作图库 + 化工计算库。国内镜像源，离线机器可改 PY_IMAGE / PIP_INDEX。
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

RUN useradd -m -u 10001 runner
COPY server.py /srv/server.py
USER runner
WORKDIR /tmp
EXPOSE 8000
CMD ["python", "/srv/server.py"]
