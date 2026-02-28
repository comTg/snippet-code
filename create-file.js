#!/usr/bin/env node

/**
 * 创建指定大小的文本文件
 *
 * 用法:
 *   node create-file.js <文件路径> <文件大小>
 *
 * 大小格式支持:
 *   - 纯数字: 按字节计算，如 1024
 *   - B:  字节，如 512B
 *   - KB: 千字节，如 10KB
 *   - MB: 兆字节，如 5MB
 *   - GB: 吉字节，如 1GB
 *
 * 示例:
 *   node create-file.js output.txt 1KB
 *   node create-file.js output.txt 10MB
 *   node create-file.js output.txt 1GB
 *   node create-file.js output.txt 2048
 */

const fs = require("fs");
const path = require("path");

// ============== 配置 ==============

// 每次写入的块大小 (64KB)，用于流式写入大文件
const CHUNK_SIZE = 64 * 1024;

// 用于填充文件的字符集
const CHARSET =
  "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";

// 每行字符数（不含换行符）
const LINE_LENGTH = 79;

// ============== 工具函数 ==============

/**
 * 解析文件大小字符串，返回字节数
 * @param {string} sizeStr - 大小字符串，如 "10MB", "1GB", "1024"
 * @returns {number} 字节数
 */
function parseSize(sizeStr) {
  const units = {
    B: 1,
    KB: 1024,
    MB: 1024 ** 2,
    GB: 1024 ** 3,
    TB: 1024 ** 4,
  };

  const match = sizeStr.toUpperCase().match(/^(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB)?$/);
  if (!match) {
    throw new Error(
      `无效的大小格式: "${sizeStr}"。支持的格式: 1024, 512B, 10KB, 5MB, 1GB, 0.5TB`
    );
  }

  const value = parseFloat(match[1]);
  const unit = match[2] || "B";
  const bytes = Math.floor(value * units[unit]);

  if (bytes <= 0) {
    throw new Error("文件大小必须大于 0");
  }

  return bytes;
}

/**
 * 将字节数格式化为人类可读的大小
 * @param {number} bytes - 字节数
 * @returns {string} 格式化后的字符串
 */
function formatSize(bytes) {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let unitIndex = 0;
  let size = bytes;

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex++;
  }

  return `${size % 1 === 0 ? size : size.toFixed(2)} ${units[unitIndex]}`;
}

/**
 * 生成一行随机文本（含换行符）
 * @param {number} length - 行长度（不含换行符）
 * @returns {string}
 */
function generateLine(length) {
  let line = "";
  for (let i = 0; i < length; i++) {
    line += CHARSET[Math.floor(Math.random() * CHARSET.length)];
  }
  return line + "\n";
}

/**
 * 生成指定字节数的文本内容块
 * @param {number} size - 需要的字节数
 * @returns {Buffer}
 */
function generateChunk(size) {
  let content = "";
  const lineWithNewline = LINE_LENGTH + 1; // 加上换行符

  while (Buffer.byteLength(content, "utf-8") < size) {
    const remaining = size - Buffer.byteLength(content, "utf-8");
    if (remaining >= lineWithNewline) {
      content += generateLine(LINE_LENGTH);
    } else {
      // 最后不足一行时，精确补齐
      for (let i = 0; i < remaining; i++) {
        content += CHARSET[Math.floor(Math.random() * CHARSET.length)];
      }
    }
  }

  // 精确截取到指定字节数
  return Buffer.from(content, "utf-8").subarray(0, size);
}

// ============== 进度显示 ==============

/**
 * 显示进度条
 * @param {number} current - 当前已写入的字节数
 * @param {number} total - 总字节数
 * @param {number} startTime - 开始时间 (ms)
 */
function showProgress(current, total, startTime) {
  const percent = ((current / total) * 100).toFixed(1);
  const elapsed = (Date.now() - startTime) / 1000;
  const speed = current / elapsed; // bytes/s
  const remaining = (total - current) / speed;

  const barLength = 30;
  const filled = Math.round((current / total) * barLength);
  const bar = "█".repeat(filled) + "░".repeat(barLength - filled);

  process.stdout.write(
    `\r  [${bar}] ${percent}% | ${formatSize(current)}/${formatSize(total)} | ` +
      `速度: ${formatSize(speed)}/s | 剩余: ${remaining.toFixed(1)}s`
  );
}

// ============== 主逻辑 ==============

async function createFile(filePath, totalBytes) {
  const resolvedPath = path.resolve(filePath);

  // 确保目标目录存在
  const dir = path.dirname(resolvedPath);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }

  console.log(`\n📄 目标文件: ${resolvedPath}`);
  console.log(`📏 目标大小: ${formatSize(totalBytes)} (${totalBytes.toLocaleString()} 字节)`);
  console.log(`⏳ 开始创建...\n`);

  const startTime = Date.now();
  const writeStream = fs.createWriteStream(resolvedPath);
  let bytesWritten = 0;

  return new Promise((resolve, reject) => {
    writeStream.on("error", reject);
    writeStream.on("finish", () => {
      const elapsed = ((Date.now() - startTime) / 1000).toFixed(2);
      process.stdout.write("\n\n");
      console.log(`✅ 文件创建成功!`);
      console.log(`   路径: ${resolvedPath}`);
      console.log(`   大小: ${formatSize(totalBytes)}`);
      console.log(`   耗时: ${elapsed} 秒`);
      resolve();
    });

    function writeNext() {
      let ok = true;
      while (ok && bytesWritten < totalBytes) {
        const remaining = totalBytes - bytesWritten;
        const chunkSize = Math.min(CHUNK_SIZE, remaining);
        const chunk = generateChunk(chunkSize);

        bytesWritten += chunk.length;
        showProgress(bytesWritten, totalBytes, startTime);

        if (bytesWritten >= totalBytes) {
          writeStream.end(chunk);
        } else {
          ok = writeStream.write(chunk);
        }
      }

      if (bytesWritten < totalBytes) {
        // 等待 drain 事件后继续写入（背压处理）
        writeStream.once("drain", writeNext);
      }
    }

    writeNext();
  });
}

// ============== 入口 ==============

function printUsage() {
  console.log(`
用法: node create-file.js <文件路径> <文件大小>

大小格式:
  纯数字    按字节计算       例: 1024
  B         字节             例: 512B
  KB        千字节 (1024B)   例: 10KB
  MB        兆字节 (1024KB)  例: 5MB
  GB        吉字节 (1024MB)  例: 1GB

示例:
  node create-file.js output.txt 1KB
  node create-file.js output.txt 10MB
  node create-file.js ./data/test.txt 1GB
`);
}

async function main() {
  const args = process.argv.slice(2);

  if (args.length < 2 || args.includes("--help") || args.includes("-h")) {
    printUsage();
    process.exit(args.includes("--help") || args.includes("-h") ? 0 : 1);
  }

  const [filePath, sizeStr] = args;

  try {
    const totalBytes = parseSize(sizeStr);
    await createFile(filePath, totalBytes);
  } catch (err) {
    console.error(`\n❌ 错误: ${err.message}`);
    process.exit(1);
  }
}

main();
