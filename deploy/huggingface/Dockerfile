# Divakar FactoryOS public demo on a Hugging Face Docker Space: SQL Server Express, the API and the worker in one container.
# The Space holds only this file and README.md; the code is cloned from GitHub at build time.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# changes whenever main moves, so a Space rebuild picks up the latest commit instead of a cached clone
ADD https://api.github.com/repos/DivakarUpadhyay/FactoryOS/commits/main /tmp/main.json
RUN git clone --depth 1 https://github.com/DivakarUpadhyay/FactoryOS.git /src
WORKDIR /src
RUN dotnet publish src/Divakar.FactoryOS.Api -c Release -r linux-x64 --self-contained -o /out/api \
 && dotnet publish src/Divakar.FactoryOS.Worker -c Release -r linux-x64 --self-contained -o /out/worker

FROM mcr.microsoft.com/mssql/server:2022-latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl jq libicu70 ca-certificates \
 && rm -rf /var/lib/apt/lists/*
# Spaces run the container as user 1000, so SQL Server's data folder and the app must belong to it
RUN (id -u 1000 >/dev/null 2>&1 || useradd -m -u 1000 user) \
 && mkdir -p /var/opt/mssql /home/user && chown -R 1000:1000 /var/opt/mssql /home/user
COPY --from=build --chown=1000:1000 /out /app
COPY --from=build --chown=1000:1000 /src/deploy/huggingface/start.sh /app/start.sh
RUN chmod +x /app/start.sh
USER 1000
ENV ACCEPT_EULA=Y MSSQL_PID=Express HOME=/home/user
EXPOSE 7860
CMD ["/app/start.sh"]
