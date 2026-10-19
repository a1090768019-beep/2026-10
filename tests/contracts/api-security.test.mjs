import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('API has a bounded per-identity fixed-window rate limiter before tenant database resolution', async () => {
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const securityReview = await read('docs/SECURITY-REVIEW.md');
  const threatModel = await read('docs/threat-model.md');

  assert.match(program, /AddRateLimiter/);
  assert.match(program, /Status429TooManyRequests/);
  assert.match(program, /PartitionedRateLimiter\.Create<HttpContext, string>/);
  assert.match(program, /FindFirst\("sub"\)/);
  assert.match(program, /RemoteIpAddress/);
  assert.match(program, /GetFixedWindowLimiter/);
  assert.match(program, /PermitLimit = 120/);
  assert.match(program, /Window = TimeSpan\.FromMinutes\(1\)/);
  assert.match(program, /QueueLimit = 0/);
  assert.match(program, /Retry-After/);

  assert.ok(program.indexOf('app.UseAuthentication();') < program.indexOf('app.UseRateLimiter();'));
  assert.ok(program.indexOf('app.UseRateLimiter();') < program.indexOf('app.UseMiddleware<TenantResolutionMiddleware>();'));
  assert.match(securityReview, /محدد معدل تطبيق ثابت النافذة/);
  assert.match(threatModel, /محدد ثابت النافذة داخل العملية/);
});
