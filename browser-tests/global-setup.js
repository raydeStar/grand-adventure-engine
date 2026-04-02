const { request } = require('@playwright/test');

module.exports = async (config) => {
  const baseURL = config.projects[0]?.use?.baseURL || config.use.baseURL;
  const client = await request.newContext({ baseURL });

  for (let attempt = 0; attempt < 15; attempt += 1) {
    try {
      const response = await client.get('/health');
      if (response.ok()) {
        await client.dispose();
        return;
      }
    } catch {
      // Retry until the stack is reachable.
    }

    await new Promise((resolve) => setTimeout(resolve, 2000));
  }

  await client.dispose();
  throw new Error(`Dashboard not reachable at ${baseURL}. Start the stack first or run npm run test:e2e:docker.`);
};