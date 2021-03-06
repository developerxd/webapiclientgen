﻿using System.Reflection;
using System.IO;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Linq;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using Fonlow.Reflection;
using Fonlow.DocComment;

namespace Fonlow.Poco2Client
{
	/// <summary>
	/// POCO to C# client data types generator
	/// </summary>
	public class Poco2CsGen : IPoco2Client
	{
		CodeCompileUnit targetUnit;

		/// <summary>
		/// Init with its own CodeCompileUnit.
		/// </summary>
		public Poco2CsGen()
		{
			targetUnit = new CodeCompileUnit();
			pendingTypes = new List<Type>();
		}

		/// <summary>
		/// Gen will share the same CodeCompileUnit with other CodeGen components.
		/// </summary>
		/// <param name="codeCompileUnit"></param>
		public Poco2CsGen(CodeCompileUnit codeCompileUnit)
		{
			targetUnit = codeCompileUnit;
			pendingTypes = new List<Type>();
		}


		/// <summary>
		/// Save TypeScript codes generated into a file.
		/// </summary>
		/// <param name="fileName"></param>
		public void SaveCodeToFile(string fileName)
		{
			if (String.IsNullOrEmpty(fileName))
				throw new ArgumentException("A valid fileName is not defined.", "fileName");

			try
			{
				using (var stream = new MemoryStream())
				using (StreamWriter writer = new StreamWriter(stream))
				{
					WriteCode(writer);
					writer.Flush();
					stream.Position = 0;
					using (var stringReader = new StreamReader(stream))
					using (var fileWriter = new StreamWriter(fileName))
					{
						var s = stringReader.ReadToEnd();
						fileWriter.Write(s.Replace("//;", ""));
					}
				}
			}
			catch (IOException e)
			{
				Trace.TraceWarning(e.Message);
			}
			catch (UnauthorizedAccessException e)
			{
				Trace.TraceWarning(e.Message);
			}
			catch (System.Security.SecurityException e)
			{
				Trace.TraceWarning(e.Message);
			}
		}

		public void WriteCode(TextWriter writer)
		{
			if (writer == null)
				throw new ArgumentNullException("writer", "No TextWriter instance is defined.");

			CodeDomProvider provider = CodeDomProvider.CreateProvider("CSharp");
			CodeGeneratorOptions options = new CodeGeneratorOptions();

			provider.GenerateCodeFromCompileUnit(targetUnit, writer, options);
		}

		string clientNamespaceSuffix;

		public void CreateCodeDom(Assembly assembly, CherryPickingMethods methods, DocCommentLookup docLookup, string clientNamespaceSuffix)
		{
			this.docLookup = docLookup;
			this.clientNamespaceSuffix = clientNamespaceSuffix;
			var cherryTypes = PodGenHelper.GetCherryTypes(assembly, methods);
			CreateCodeDom(cherryTypes, methods, clientNamespaceSuffix);
		}

		DocCommentLookup docLookup;

		void CreateTypeDocComment(Type type, CodeTypeDeclaration typeDeclaration)
		{
			if (docLookup != null)
			{
				var dm = docLookup.GetMember("T:" + type.FullName);
				AddDocComments(dm, typeDeclaration.Comments);
			}
		}

		void CreatePropertyDocComment(PropertyInfo propertyInfo, CodeTypeMember codeField)
		{
			if (docLookup != null)
			{
				var propertyFullName = propertyInfo.DeclaringType.FullName + "." + propertyInfo.Name;
				var dm = docLookup.GetMember("P:" + propertyFullName);
				AddDocComments(dm, codeField.Comments);
			}
		}

		void CreateFieldDocComment(FieldInfo fieldInfo, CodeTypeMember codeField)
		{
			if (docLookup != null)
			{
				var propertyFullName = fieldInfo.DeclaringType.FullName + "." + fieldInfo.Name;
				var dm = docLookup.GetMember("F:" + propertyFullName);
				AddDocComments(dm, codeField.Comments);
			}
		}

		static void AddDocComments(docMember member, CodeCommentStatementCollection comments)
		{
			if (member != null && comments != null)
			{
				if (member.summary != null)
				{
					comments.Add(new CodeCommentStatement("<summary>", true));
					var noIndent = StringFunctions.TrimTrimIndentsOfArray(member.summary.Text);
					if (noIndent != null)
					{
						foreach (var item in noIndent)
						{
							comments.Add(new CodeCommentStatement(item, true));
						}
					}

					comments.Add(new CodeCommentStatement("</summary>", true));
				}
			}
		}

		List<Type> pendingTypes;

		/// <summary>
		/// Create TypeScript CodeDOM for POCO types. 
		/// For an enum type, all members will be processed regardless of EnumMemberAttribute.
		/// </summary>
		/// <param name="types">POCO types.</param>
		/// <param name="methods">How to cherry pick data to be exposed to the clients.</param>
		public void CreateCodeDom(Type[] types, CherryPickingMethods methods, string clientNamespaceSuffix)
		{
			if (types == null)
				throw new ArgumentNullException("types", "types is not defined.");

			this.pendingTypes.AddRange(types);
			var typeGroupedByNamespace = types
				.GroupBy(d => d.Namespace)
				.OrderBy(k => k.Key); // order by namespace
			var namespacesOfTypes = typeGroupedByNamespace.Select(d => d.Key).ToArray();
			foreach (var groupedTypes in typeGroupedByNamespace)
			{
				var clientNamespaceText = (groupedTypes.Key + clientNamespaceSuffix);
				var clientNamespace = new CodeNamespace(clientNamespaceText);
				targetUnit.Namespaces.Add(clientNamespace);//namespace added to Dom

				Debug.WriteLine("Generating types in namespace: " + groupedTypes.Key + " ...");
				groupedTypes.OrderBy(t => t.Name).Select(type =>
				{
					var tsName = type.Name;
					Debug.WriteLine("clientClass: " + clientNamespace + "  " + tsName);

					CodeTypeDeclaration typeDeclaration;
					if (TypeHelper.IsClassOrStruct(type))
					{
						if (type.IsGenericType)
						{
							typeDeclaration = PodGenHelper.CreatePodClientGenericClass(clientNamespace, type);
						}
						else
						{
							typeDeclaration = type.IsClass ? PodGenHelper.CreatePodClientClass(clientNamespace, tsName) : PodGenHelper.CreatePodClientStruct(clientNamespace, tsName);
						}

						if (!type.IsValueType)
						{
							if (namespacesOfTypes.Contains(type.BaseType.Namespace))
							{
								typeDeclaration.BaseTypes.Add(RefineCustomComplexTypeText(type.BaseType));
							}
							else
							{
								typeDeclaration.BaseTypes.Add(type.BaseType);
							}
						}

						CreateTypeDocComment(type, typeDeclaration);

						var typeProperties = type.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name).ToArray();
						foreach (var propertyInfo in typeProperties)
						{
							var cherryType = CherryPicking.GetMemberCherryType(propertyInfo, methods);
							if (cherryType == CherryType.None)
								continue;
							string tsPropertyName;


							//todo: Maybe the required of JsonMemberAttribute?       var isRequired = cherryType == CherryType.BigCherry;
							tsPropertyName = propertyInfo.Name;//todo: String.IsNullOrEmpty(dataMemberAttribute.Name) ? propertyInfo.Name : dataMemberAttribute.Name;
							Debug.WriteLine(String.Format("{0} : {1}", tsPropertyName, propertyInfo.PropertyType.Name));


							//var clientProperty = new CodeMemberProperty()
							//{
							// Name = tsPropertyName,
							// Type = TranslateToClientTypeReference(propertyInfo.PropertyType),
							// Attributes = MemberAttributes.Public | MemberAttributes.Final,
							//};
							var clientProperty = CreateProperty(tsPropertyName, propertyInfo.PropertyType);

							var isRequired = cherryType == CherryType.BigCherry;
							if (isRequired)
							{
								clientProperty.CustomAttributes.Add(new CodeAttributeDeclaration("System.ComponentModel.DataAnnotations.RequiredAttribute"));
							}

							//var privateFieldName = "_" + tsPropertyName;

							//typeDeclaration.Members.Add(new CodeMemberField()
							//{
							// Name = privateFieldName,
							// Type = TranslateToClientTypeReference(propertyInfo.PropertyType),
							//});

							//clientProperty.GetStatements.Add(new CodeSnippetStatement($"\t\t\t\treturn {privateFieldName};"));
							//clientProperty.SetStatements.Add(new CodeSnippetStatement($"\t\t\t\t{privateFieldName} = value;"));

							CreatePropertyDocComment(propertyInfo, clientProperty);

							typeDeclaration.Members.Add(clientProperty);
						}

						var typeFields = type.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public).OrderBy(f => f.Name).ToArray();
						foreach (var fieldInfo in typeFields)
						{
							var cherryType = CherryPicking.GetMemberCherryType(fieldInfo, methods);
							if (cherryType == CherryType.None)
								continue;
							string tsPropertyName;


							tsPropertyName = fieldInfo.Name;//todo: String.IsNullOrEmpty(dataMemberAttribute.Name) ? propertyInfo.Name : dataMemberAttribute.Name;
							Debug.WriteLine(String.Format("{0} : {1}", tsPropertyName, fieldInfo.FieldType.Name));

							//public fields of a class will be translated into properties
							if (type.IsClass)
							{
								//var clientProperty = new CodeMemberProperty()
								//{
								// Name = tsPropertyName,
								// Type = TranslateToClientTypeReference(fieldInfo.FieldType),
								// Attributes = MemberAttributes.Public | MemberAttributes.Final,
								//};

								var clientProperty = CreateProperty(tsPropertyName, fieldInfo.FieldType);

								var isRequired = cherryType == CherryType.BigCherry;
								if (isRequired)
								{
									clientProperty.CustomAttributes.Add(new CodeAttributeDeclaration("System.ComponentModel.DataAnnotations.RequiredAttribute"));
								}

								//var privateFieldName = "_" + tsPropertyName;

								//typeDeclaration.Members.Add(new CodeMemberField()
								//{
								// Name = privateFieldName,
								// Type = TranslateToClientTypeReference(fieldInfo.FieldType),
								//});

								//clientProperty.GetStatements.Add(new CodeSnippetStatement($"\t\t\t\treturn {privateFieldName};"));
								//clientProperty.SetStatements.Add(new CodeSnippetStatement($"\t\t\t\t{privateFieldName} = value;"));

								CreateFieldDocComment(fieldInfo, clientProperty);

								typeDeclaration.Members.Add(clientProperty);
							}
							else //public fields of struct
							{
								var clientField = new CodeMemberField()
								{
									Name = tsPropertyName,
									Type = TranslateToClientTypeReference(fieldInfo.FieldType),
									Attributes = MemberAttributes.Public | MemberAttributes.Final,
									//todo: add some attributes                               
								};

								CreateFieldDocComment(fieldInfo, clientField);

								typeDeclaration.Members.Add(clientField);
							}
						}
					}
					else if (type.IsEnum)
					{
						typeDeclaration = PodGenHelper.CreatePodClientEnum(clientNamespace, tsName);

						CreateTypeDocComment(type, typeDeclaration);

						int k = 0;
						foreach (var fieldInfo in type.GetFields(BindingFlags.Public | BindingFlags.Static))//not to sort
						{
							var name = fieldInfo.Name;
							var intValue = (int)Convert.ChangeType(fieldInfo.GetValue(null), typeof(int));
							Debug.WriteLine(name + " -- " + intValue);
							var isInitialized = intValue != k;

							var clientField = new CodeMemberField()
							{
								Name = name,
								Type = new CodeTypeReference(fieldInfo.FieldType),
								InitExpression = isInitialized ? new CodePrimitiveExpression(intValue) : null,
							};

							CreateFieldDocComment(fieldInfo, clientField);

							typeDeclaration.Members.Add(clientField);
							k++;
						}

					}
					else
					{
						Trace.TraceWarning("Not yet supported: " + type.Name);
						typeDeclaration = null;
					}

					return typeDeclaration;
				}
					).ToArray();//add classes into the namespace
			}


		}

		CodeMemberField CreateProperty(string name, Type type)
		{
			// This is a little hack. Since you cant create auto properties in CodeDOM,
			//  we make the getter and setter part of the member name.
			// This leaves behind a trailing semicolon that we comment out.
			//  Later, we remove the commented out semicolons.
			string memberName = name + " { get; set; }//";

			CodeMemberField result = new CodeMemberField() { Type = TranslateToClientTypeReference(type), Name = memberName };
			result.Attributes = MemberAttributes.Public | MemberAttributes.Final;
			return result;
		}

		public CodeTypeReference TranslateToClientTypeReference(Type type)
		{
			if (type == null)
				return null;// new CodeTypeReference("void");

			if (pendingTypes.Contains(type))
				return new CodeTypeReference(RefineCustomComplexTypeText(type));
			else if (type.IsGenericType)
			{
				return TranslateGenericToTypeReference(type);
			}
			else if (type.IsArray)
			{
				Debug.Assert(type.Name.EndsWith("]"));
				var elementType = type.GetElementType();
				var arrayRank = type.GetArrayRank();
				return CreateArrayTypeReference(elementType, arrayRank);
			}
			else
			{
				if (type.FullName == "System.Web.Http.IHttpActionResult")
					return new CodeTypeReference("System.Net.Http.HttpResponseMessage");

				if (type.FullName == "Microsoft.AspNetCore.Mvc.IActionResult" || type.FullName == "Microsoft.AspNetCore.Mvc.ActionResult")
					return new CodeTypeReference("System.Net.Http.HttpResponseMessage");

				if (type.FullName == "System.Net.Http.HttpResponseMessage")
					return new CodeTypeReference("System.Net.Http.HttpResponseMessage");

				if (type.FullName == "System.Object" && (type.Attributes & System.Reflection.TypeAttributes.Serializable) == System.Reflection.TypeAttributes.Serializable)
					return new CodeTypeReference("Newtonsoft.Json.Linq.JObject");
			}


			return new CodeTypeReference(type);

		}

		CodeTypeReference TranslateGenericToTypeReference(Type type)
		{
			Type genericTypeDefinition = type.GetGenericTypeDefinition();
			Type[] genericArguments = type.GetGenericArguments();

			if (genericTypeDefinition == typeof(Nullable<>))
			{
				return new CodeTypeReference(typeof(Nullable).FullName
					, TranslateToClientTypeReference(genericArguments[0]));
			}

			if (genericTypeDefinition == typeof(System.Threading.Tasks.Task<>))
			{
				return TranslateToClientTypeReference(genericArguments[0]);
			}

			//Handle array types
			if (TypeHelper.IsArrayType(genericTypeDefinition))
			{
				Debug.Assert(type.GenericTypeArguments.Length == 1);
				var elementType = type.GenericTypeArguments[0];
				return CreateArrayTypeReference(elementType, 1);
			}

			var tupleTypeIndex = TypeHelper.IsTuple(genericTypeDefinition);
			if (tupleTypeIndex >= 0)
			{
				switch (tupleTypeIndex)
				{
					case 0:
						Debug.Assert(genericArguments.Length == 1);
						return new CodeTypeReference(TypeHelper.TupleTypeNames[0]
							, TranslateToClientTypeReference(genericArguments[0]));
					case 1:
						Debug.Assert(genericArguments.Length == 2);
						return new CodeTypeReference(TypeHelper.TupleTypeNames[1]
							, TranslateToClientTypeReference(genericArguments[0])
							, TranslateToClientTypeReference(genericArguments[1]));
					case 2:
						return new CodeTypeReference(TypeHelper.TupleTypeNames[2]
							, TranslateToClientTypeReference(genericArguments[0])
							, TranslateToClientTypeReference(genericArguments[1])
							, TranslateToClientTypeReference(genericArguments[2]));
					case 3:
						return new CodeTypeReference(TypeHelper.TupleTypeNames[3]
							, TranslateToClientTypeReference(genericArguments[0])
							, TranslateToClientTypeReference(genericArguments[1])
							, TranslateToClientTypeReference(genericArguments[2])
							, TranslateToClientTypeReference(genericArguments[3]));
					case 4:
						return new CodeTypeReference(TypeHelper.TupleTypeNames[4]
							, TranslateToClientTypeReference(genericArguments[0])
							, TranslateToClientTypeReference(genericArguments[1])
							, TranslateToClientTypeReference(genericArguments[2])
							, TranslateToClientTypeReference(genericArguments[3])
							, TranslateToClientTypeReference(genericArguments[4]));
					case 5:
						return new CodeTypeReference(TypeHelper.TupleTypeNames[5]
							, TranslateToClientTypeReference(genericArguments[0])
							, TranslateToClientTypeReference(genericArguments[1])
							, TranslateToClientTypeReference(genericArguments[2])
							, TranslateToClientTypeReference(genericArguments[3])
							, TranslateToClientTypeReference(genericArguments[4])
							, TranslateToClientTypeReference(genericArguments[5]));
					case 6:
						return new CodeTypeReference(TypeHelper.TupleTypeNames[6]
							, TranslateToClientTypeReference(genericArguments[0])
							, TranslateToClientTypeReference(genericArguments[1])
							, TranslateToClientTypeReference(genericArguments[2])
							, TranslateToClientTypeReference(genericArguments[3])
							, TranslateToClientTypeReference(genericArguments[4])
							, TranslateToClientTypeReference(genericArguments[5])
							, TranslateToClientTypeReference(genericArguments[6]));
					case 7:
						Debug.Assert(genericArguments.Length == 8);
						return new CodeTypeReference(TypeHelper.TupleTypeNames[7]
							, TranslateToClientTypeReference(genericArguments[0])
							, TranslateToClientTypeReference(genericArguments[1])
							, TranslateToClientTypeReference(genericArguments[2])
							, TranslateToClientTypeReference(genericArguments[3])
							, TranslateToClientTypeReference(genericArguments[4])
							, TranslateToClientTypeReference(genericArguments[5])
							, TranslateToClientTypeReference(genericArguments[6])
							, TranslateToClientTypeReference(genericArguments[7]));
					default:
						throw new InvalidOperationException("Hey, what Tuple");
				}
			}


			if (genericArguments.Length == 2)
			{
				if (genericTypeDefinition == typeof(IDictionary<,>))
				{
					return new CodeTypeReference(typeof(Dictionary<,>).FullName,
						TranslateToClientTypeReference(genericArguments[0]), TranslateToClientTypeReference(genericArguments[1]));
				}

				Type closedDictionaryType = typeof(IDictionary<,>).MakeGenericType(genericArguments[0], genericArguments[1]);
				if (closedDictionaryType.IsAssignableFrom(type))
				{
					return new CodeTypeReference(typeof(Dictionary<,>).FullName,
						TranslateToClientTypeReference(genericArguments[0]), TranslateToClientTypeReference(genericArguments[1]));
				}

				if (genericTypeDefinition == typeof(KeyValuePair<,>))
				{
					return new CodeTypeReference(typeof(KeyValuePair<,>).FullName,
						TranslateToClientTypeReference(genericArguments[0]), TranslateToClientTypeReference(genericArguments[1]));
				}


			}

			return new CodeTypeReference(RefineCustomComplexTypeText(genericTypeDefinition), genericArguments.Select(t => TranslateToClientTypeReference(t)).ToArray());

		}

		string RefineCustomComplexTypeText(Type t)
		{
			return t.Namespace + this.clientNamespaceSuffix + "." + t.Name;
		}

		CodeTypeReference CreateArrayTypeReference(Type elementType, int arrayRank)
		{
			if (pendingTypes.Contains(elementType))
			{
				return CreateArrayOfCustomTypeReference(elementType, arrayRank);
			}

			var otherArrayType = new CodeTypeReference(new CodeTypeReference(), arrayRank)//CodeDom does not care. The baseType is always overwritten by ArrayElementType.
			{
				ArrayElementType = TranslateToClientTypeReference(elementType),
			};
			return otherArrayType;
		}

		CodeTypeReference CreateArrayOfCustomTypeReference(Type elementType, int arrayRank)
		{
			var elementTypeReference = new CodeTypeReference(RefineCustomComplexTypeText(elementType));
			var typeReference = new CodeTypeReference(new CodeTypeReference(), arrayRank)
			{
				ArrayElementType = elementTypeReference,
			};
			return typeReference;
		}

	}

}
