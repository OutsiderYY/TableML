﻿#region Copyright (c) 2015 KEngine / Kelly <http://github.com/mr-kelly>, All rights reserved.

// KEngine - Toolset and framework for Unity3D
// ===================================
// 
// Filename: SettingModuleEditor.cs
// Date:     2015/12/03
// Author:  Kelly
// Email: 23110388@qq.com
// Github: https://github.com/mr-kelly/KEngine
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 3.0 of the License, or (at your option) any later version.
// 
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public
// License along with this library.

#endregion
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TableML.Compiler
{

    /// <summary>
    /// Compile Excel to TSV
    /// </summary>
    public class Compiler
    {

        /// <summary>
        /// 编译时，判断格子的类型
        /// </summary>
        public enum CellType
        {
            Value,
            Comment,
            If,
            Endif
        }

        private readonly CompilerConfig _config;

        public Compiler()
            : this(new CompilerConfig()
            {
            })
        {
        }

        public Compiler(CompilerConfig cfg)
        {
            _config = cfg;
        }

        /// <summary>
        /// 生成tml文件内容
        /// </summary>
        /// <param name="path">源excel的相对路径</param>
        /// <param name="excelFile"></param>
        /// <param name="compileToFilePath">编译后的tml文件路径</param>
        /// <param name="compileBaseDir"></param>
        /// <param name="doCompile"></param>
        /// <returns></returns>
        private TableCompileResult DoCompilerExcelReader(string path, ITableSourceFile excelFile, string compileToFilePath = null, string compileBaseDir = null, bool doCompile = true)
        {
            var renderVars = new TableCompileResult();
            renderVars.ExcelFile = excelFile;
            renderVars.FieldsInternal = new List<TableColumnVars>();

            var tableBuilder = new StringBuilder();
            var rowBuilder = new StringBuilder();
            var ignoreColumns = new HashSet<int>();
            // Header Column
            foreach (var colNameStr in excelFile.ColName2Index.Keys)
            {
                if (string.IsNullOrEmpty(colNameStr))
                {
                    continue;
                }
                var colIndex = excelFile.ColName2Index[colNameStr];
                var isCommentColumn = CheckCellType(colNameStr) == CellType.Comment;
                if (isCommentColumn)
                {
                    ignoreColumns.Add(colIndex);
                }
                else
                {
                    //NOTE by qingqing-zhao 分隔符为\t 。如果从指定的列开始读取，但是dict的索引是从0开始
                    if (colIndex > 0)
                    {
                        tableBuilder.Append("\t");
                    }
                    tableBuilder.Append(colNameStr);

                    string typeName = "string";
                    string defaultVal = "";

                    var attrs = excelFile.ColName2Statement[colNameStr]
                        .Split(new char[] {'|', '/'}, StringSplitOptions.RemoveEmptyEntries);
                    // Type
                    if (attrs.Length > 0)
                    {
                        typeName = attrs[0];
                    }
                    // Default Value
                    if (attrs.Length > 1)
                    {
                        defaultVal = attrs[1];
                    }
                    if (attrs.Length > 2)
                    {
                        if (attrs[2] == "pk")
                        {
                            renderVars.PrimaryKey = colNameStr;
                        }
                    }

                    renderVars.FieldsInternal.Add(new TableColumnVars
                    {
                        Index = colIndex - ignoreColumns.Count, // count the comment columns
                        Type = typeName,
                        Name = colNameStr,
                        DefaultValue = defaultVal,
                        Comment = excelFile.ColName2Comment[colNameStr],
                    });
                }
            }
            tableBuilder.Append("\n");
            //以上是tml写入的第一行

            // Statements rows, keeps
            foreach (var kv in excelFile.ColName2Statement)
            {
                var statementStr = kv.Value;
                if (string.IsNullOrEmpty(statementStr))
                {
                    continue;
                }
                var colName = kv.Key;
                var colIndex = excelFile.ColName2Index[colName];

                if (ignoreColumns.Contains(colIndex)) // comment column, ignore
                    continue;
                //NOTE by qingqing-zhao 加入\t，从指定的列开始读取，但是dict的索引是从0开始
                if (colIndex > 0)
                {
                    tableBuilder.Append("\t");
                }
                tableBuilder.Append(statementStr);
            }
            tableBuilder.Append("\n");
            //以上是tml写入的第二行


            // #if check, 是否正在if false模式, if false时，行被忽略
            var ifCondtioning = true;
            if (doCompile)
            {
                // 如果不需要真编译，获取头部信息就够了
                // Data Rows
                for (var startRow = 0; startRow < excelFile.GetRowsCount(); startRow++)
                {
                    rowBuilder.Length = 0;
                    rowBuilder.Capacity = 0;
                    var columnCount = excelFile.GetColumnCount();
                    for (var loopColumn = 0; loopColumn < columnCount; loopColumn++)
                    {
                        if (!ignoreColumns.Contains(loopColumn)) // comment column, ignore 注释列忽略
                        {
                            if (excelFile.Index2ColName.ContainsKey(loopColumn) == false)
                            {
                                continue;
                            }
                            var columnName = excelFile.Index2ColName[loopColumn];
                            var cellStr = excelFile.GetString(columnName, startRow);

                            if (loopColumn == 0)
                            {
                                var cellType = CheckCellType(cellStr);
                                if (cellType == CellType.Comment) // 如果行首为#注释字符，忽略这一行)
                                    break;

                                // 进入#if模式
                                if (cellType == CellType.If)
                                {
                                    var ifVars = GetIfVars(cellStr);
                                    var hasAllVars = true;
                                    foreach (var var in ifVars)
                                    {
                                        if (_config.ConditionVars == null ||
                                            !_config.ConditionVars.Contains(var)) // 定义的变量，需要全部配置妥当,否则if失败
                                        {
                                            hasAllVars = false;
                                            break;
                                        }
                                    }
                                    ifCondtioning = hasAllVars;
                                    break;
                                }
                                if (cellType == CellType.Endif)
                                {
                                    ifCondtioning = true;
                                    break;
                                }

                                if (!ifCondtioning) // 这一行被#if 忽略掉了
                                    break;


                                if (startRow != 0) // 不是第一行，往添加换行，首列
                                    rowBuilder.Append("\n");
                            }
                            /*
                                NOTE by qingqing-zhao 因为是从指定的列开始读取，所以>有效列 才加入\t
                                如果这列是空白的也不需要加入
                            */
                            if (!string.IsNullOrEmpty(columnName)
                               && loopColumn > 0
                               && loopColumn < columnCount) // 最后一列不需加tab
                            {
                                rowBuilder.Append("\t");
                            }

                            // 如果单元格是字符串，换行符改成\\n
                            cellStr = cellStr.Replace("\n", "\\n");
                            rowBuilder.Append(cellStr);

                        }
                    }

                    // 如果这行，之后\t或换行符，无其它内容，认为是可以省略的
                    if (!string.IsNullOrEmpty(rowBuilder.ToString().Trim()))
                        tableBuilder.Append(rowBuilder);
                }
            }
            //以上是tml写入其它行


            var fileName = Path.GetFileNameWithoutExtension(path);
            string exportPath;
            if (!string.IsNullOrEmpty(compileToFilePath))
            {
                exportPath = compileToFilePath;
            }
            else
            {
                // use default
                exportPath = fileName + _config.ExportTabExt;
            }

            var exportDirPath = Path.GetDirectoryName(exportPath);
            if (!Directory.Exists(exportDirPath))
                Directory.CreateDirectory(exportDirPath);

            // 是否写入文件
            if (doCompile)
            {
                var dirName = Path.GetDirectoryName(exportPath);
                if (Directory.Exists(dirName) == false)
                {
                    Directory.CreateDirectory(dirName);
                }
                File.WriteAllText(exportPath, tableBuilder.ToString());
            }


            // 基于base dir路径
            var tabFilePath = exportPath; // without extension
            var fullTabFilePath = Path.GetFullPath(tabFilePath).Replace("\\", "/"); ;
            if (!string.IsNullOrEmpty(compileBaseDir))
            {
                var fullCompileBaseDir = Path.GetFullPath(compileBaseDir).Replace("\\", "/"); ;
                tabFilePath = fullTabFilePath.Replace(fullCompileBaseDir, ""); // 保留后戳
            }
            if (tabFilePath.StartsWith("/"))
                tabFilePath = tabFilePath.Substring(1);

            renderVars.TabFileFullPath = fullTabFilePath;
            renderVars.TabFileRelativePath = tabFilePath;

            return renderVars;
        }

        /// <summary>
        /// 获取#if A B语法的变量名，返回如A B数组
        /// </summary>
        /// <param name="cellStr"></param>
        /// <returns></returns>
        private string[] GetIfVars(string cellStr)
        {
            return cellStr.Replace("#if", "").Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 检查一个表头名，是否是可忽略的注释
        /// 或检查一个字符串
        /// </summary>
        /// <param name="colNameStr"></param>
        /// <returns></returns>
        private CellType CheckCellType(string colNameStr)
        {
            if (colNameStr.StartsWith("#if"))
                return CellType.If;
            if (colNameStr.StartsWith("#endif"))
                return CellType.Endif;
            foreach (var commentStartsWith in _config.CommentStartsWith)
            {
                if (colNameStr.ToLower().Trim().StartsWith(commentStartsWith.ToLower()))
                {
                    return CellType.Comment;
                }
            }

            return CellType.Value;
        }

        /// <summary>
        /// Compile the specified path, auto change extension to config `ExportTabExt`
        /// </summary>
        /// <param name="path">Path.</param>
        public TableCompileResult Compile(string path)
        {
            var outputPath = System.IO.Path.ChangeExtension(path, this._config.ExportTabExt);
            return Compile(path, outputPath);
        }

        /// <summary>
        /// Compile a setting file, return a hash for template
        /// </summary>
        /// <param name="path"></param>
        /// <param name="compileToFilePath"></param>
        /// <param name="compileBaseDir"></param>
        /// <param name="doRealCompile">Real do, or just get the template var?</param>
        /// <returns></returns>
        public TableCompileResult Compile(string path, string compileToFilePath, int index=0, string compileBaseDir = null, bool doRealCompile = true)
        {
            // 确保目录存在
            compileToFilePath = Path.GetFullPath(compileToFilePath);
            var compileToFileDirPath = Path.GetDirectoryName(compileToFilePath);

            if (!Directory.Exists(compileToFileDirPath))
                Directory.CreateDirectory(compileToFileDirPath);

            var ext = Path.GetExtension(path);

            ITableSourceFile sourceFile = null;
            if (ext == ".tsv")
            {
                sourceFile = new SimpleTSVFile(path);
            }
            else if (ext.Contains(".xls"))
            {
                sourceFile = new SimpleExcelFile(path, index);
            }
            else if (ext == ".csv")
            {
                sourceFile = new SimpleCSVFile(path);
            }

            var hash = DoCompilerExcelReader(path, sourceFile, compileToFilePath, compileBaseDir, doRealCompile);
            return hash;

        }
    }
}