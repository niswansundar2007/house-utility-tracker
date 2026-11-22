# Runs the same C# backend on Linux using Mono (for free cloud hosts such as Render)
FROM mono:6.12

WORKDIR /app
COPY src/ src/
RUN mkdir -p out/public \
 && mcs -optimize+ -r:System.Web.Extensions.dll -out:out/HouseUtility.exe src/Server.cs \
 && cp src/public/* out/public/

ENV TZ=Asia/Kolkata
EXPOSE 10000
CMD ["mono", "out/HouseUtility.exe", "--no-browser"]
