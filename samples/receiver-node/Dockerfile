# No dependencies to install: node:http and node:crypto only.
FROM node:22-alpine
WORKDIR /app
COPY package.json standard-webhooks.mjs receiver.mjs ./
USER node
ENV PORT=8080
EXPOSE 8080
CMD ["node", "receiver.mjs"]
