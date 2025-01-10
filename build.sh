cd "$(dirname "$0")"
git pull
git submodule update --init
dotnet build -c Release FFXIVClientStructs/FFXIVClientStructs/FFXIVClientStructs.csproj
docfx
