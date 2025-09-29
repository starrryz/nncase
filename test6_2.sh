export DOTNET_ReadyToRun=0
export COMPlus_ZapDisable=1
export COMPlus_TieredPGO=1

# 对齐OUT目录
OUT="pytest_trace_$(date +%m%d_%H%M%S)"
mkdir -p "$OUT"
echo "Use OUT=$OUT"

PID=""
while :; do
  PID=$(dotnet-trace ps | awk '/pytest|python3/ {print $1; exit}' || true)
  [ -n "$PID" ] && break
  sleep 0.5
done
echo "PID=$PID"

# 2) 需要时（例如你看到日志已经进入 AutoDist）再手动启动采样
dotnet-trace collect \
  --profile cpu-sampling \
  -p "$PID" \
  --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x38:5,Microsoft-Windows-DotNETRuntimeRundown:0x38:5 \
  --buffersize 2048 \
  --duration 00:28:00 \
  -o "$OUT/pytest.nettrace"

dotnet-trace convert "$OUT/pytest.nettrace" --format speedscope -o "$OUT/pytest.speedscope.json" >/dev/null 

echo "Speedscope: $OUT/pytest.speedscope.json"

  # --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x4c14fccbd:5,Microsoft-Windows-DotNETRuntimeRundown:0x4c14fccbd:5 \
