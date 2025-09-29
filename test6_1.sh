export DOTNET_ReadyToRun=0
export COMPlus_ZapDisable=1
export COMPlus_TieredPGO=1


# 实时打印 + 写入日志，供终端B监听关键词（可选）
pytest -vvs tests/importer/huggingface_/disabled_test_qwen3.py
