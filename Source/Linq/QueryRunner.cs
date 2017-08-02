﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

#if !SL4
using System.Threading.Tasks;
#endif

namespace LinqToDB.Linq
{
	using Common;
	using Data;
	using LinqToDB.Expressions;
	using SqlQuery;

	static class QueryRunner
	{
		#region Mapper

		class Mapper<T>
		{
			public Mapper(Expression<Func<IQueryRunner,IDataReader,T>> mapperExpression)
			{
				_expression = mapperExpression;
			}

			readonly Expression<Func<IQueryRunner,IDataReader,T>> _expression;
			         Expression<Func<IQueryRunner,IDataReader,T>> _mapperExpression;
			                    Func<IQueryRunner,IDataReader,T>  _mapper;

			bool _isFaulted;

			public IQueryRunner QueryRunner;

			public T Map(IQueryRunner queryRunner, IDataReader dataReader)
			{
				if (_mapper == null)
				{
					_mapperExpression = (Expression<Func<IQueryRunner,IDataReader,T>>)_expression.Transform(e =>
					{
						var ex = e as ConvertFromDataReaderExpression;
						return ex != null ? ex.Reduce(dataReader) : e;
					});

					var qr = QueryRunner;
					if (qr != null)
						qr.MapperExpression = _mapperExpression;

					_mapper = _mapperExpression.Compile();
				}

				try
				{
					return _mapper(queryRunner, dataReader);
				}
				catch (FormatException ex)
				{
					if (_isFaulted)
						throw;

#if !SILVERLIGHT && !NETFX_CORE
					if (DataConnection.TraceSwitch.TraceInfo)
						DataConnection.WriteTraceLine(
							"Mapper has switched to slow mode. Mapping exception: " + ex.Message,
							DataConnection.TraceSwitch.DisplayName);
#endif

					_isFaulted = true;

					var qr = QueryRunner;
					if (qr != null)
						qr.MapperExpression = _mapperExpression;

					return (_mapper = _expression.Compile())(queryRunner, dataReader);
				}
				catch (InvalidCastException ex)
				{
					if (_isFaulted)
						throw;

#if !SILVERLIGHT && !NETFX_CORE
					if (DataConnection.TraceSwitch.TraceInfo)
						DataConnection.WriteTraceLine(
							"Mapper has switched to slow mode. Mapping exception: " + ex.Message,
							DataConnection.TraceSwitch.DisplayName);
#endif

					_isFaulted = true;

					var qr = QueryRunner;
					if (qr != null)
						qr.MapperExpression = _mapperExpression;

					return (_mapper = _expression.Compile())(queryRunner, dataReader);
				}
			}
		}

		#endregion

		#region Helpers

		static void FinalizeQuery(Query query)
		{
			foreach (var sql in query.Queries)
			{
				sql.SelectQuery = query.SqlOptimizer.Finalize(sql.SelectQuery);
				sql.Parameters  = sql.Parameters
					.Select (p => new { p, idx = sql.SelectQuery.Parameters.IndexOf(p.SqlParameter) })
					.OrderBy(p => p.idx)
					.Select (p => p.p)
					.ToList();
			}
		}

		static void ClearParameters(Query query)
		{
			foreach (var q in query.Queries)
				foreach (var sqlParameter in q.Parameters)
					sqlParameter.Expression = null;
		}

		static int GetParameterIndex(Query query, ISqlExpression parameter)
		{
			var parameters = query.Queries[0].Parameters;

			for (var i = 0; i < parameters.Count; i++)
			{
				var p = parameters[i].SqlParameter;

				if (p == parameter)
					return i;
			}

			throw new InvalidOperationException();
		}

		#endregion

		#region SetRunQuery

		static Tuple<
			Func<Query,QueryContext,IDataContextEx,Mapper<T>,Expression,object[],int,IEnumerable<T>>,
			Func<Expression,object[],int>,
			Func<Expression,object[],int>>
			GetExecuteQuery<T>(
				Query query,
				Func<Query,QueryContext,IDataContextEx,Mapper<T>,Expression,object[],int,IEnumerable<T>> queryFunc)
		{
			FinalizeQuery(query);

			if (query.Queries.Count != 1)
				throw new InvalidOperationException();

			Func<Expression,object[],int> skip = null, take = null;

			var select = query.Queries[0].SelectQuery.Select;

			if (select.SkipValue != null && !query.SqlProviderFlags.GetIsSkipSupportedFlag(query.Queries[0].SelectQuery))
			{
				var q = queryFunc;

				var value = select.SkipValue as SqlValue;
				if (value != null)
				{
					var n = (int)((IValueContainer)select.SkipValue).Value;

					if (n > 0)
					{
						queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Skip(n);
						skip  = (expr, ps) => n;
					}
				}
				else if (select.SkipValue is SqlParameter)
				{
					var i = GetParameterIndex(query, select.SkipValue);
					queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Skip((int)query.Queries[0].Parameters[i].Accessor(expr, ps));
					skip  = (expr,ps) => (int)query.Queries[0].Parameters[i].Accessor(expr, ps);
				}
			}

			if (select.TakeValue != null && !query.SqlProviderFlags.IsTakeSupported)
			{
				var q = queryFunc;

				var value = select.TakeValue as SqlValue;
				if (value != null)
				{
					var n = (int)((IValueContainer)select.TakeValue).Value;

					if (n > 0)
					{
						queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Take(n);
						take  = (expr, ps) => n;
					}
				}
				else if (select.TakeValue is SqlParameter)
				{
					var i = GetParameterIndex(query, select.TakeValue);
					queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Take((int)query.Queries[0].Parameters[i].Accessor(expr, ps));
					take  = (expr,ps) => (int)query.Queries[0].Parameters[i].Accessor(expr, ps);
				}
			}

			return Tuple.Create(queryFunc, skip, take);
		}

		static IEnumerable<T> ExecuteQuery<T>(
			Query          query,
			QueryContext   queryContext,
			IDataContextEx dataContext,
			Mapper<T>      mapper,
			Expression     expression,
			object[]       ps,
			int            queryNumber)
		{
			if (queryContext == null)
				queryContext = new QueryContext(dataContext, expression, ps);

			using (var runner = dataContext.GetQueryRunner(query, queryNumber, expression, ps))
			{
				runner.QueryContext = queryContext;
				runner.DataContext  = dataContext;

				try
				{
					mapper.QueryRunner = runner;

					using (var dr = runner.ExecuteReader())
					{
						while (dr.Read())
						{
							yield return mapper.Map(runner, dr);
							runner.RowsCount++;
						}
					}
				}
				finally
				{
					mapper.QueryRunner = null;
				}
			}
		}

#if !NOASYNC

		static async Task ExecuteQueryAsync<T>(
			Query                         query,
			QueryContext                  queryContext,
			IDataContextEx                dataContext,
			Mapper<T>                     mapper,
			Expression                    expression,
			object[]                      ps,
			int                           queryNumber,
			Func<T,bool>                  func,
			Func<Expression,object[],int> skipAction,
			Func<Expression,object[],int> takeAction,
			CancellationToken             cancellationToken,
			TaskCreationOptions           options)
		{
			if (queryContext == null)
				queryContext = new QueryContext(dataContext, expression, ps);

			using (var runner = dataContext.GetQueryRunner(query, queryNumber, expression, ps))
			{
				Func<IDataReader,T> m = dr => mapper.Map(runner, dr);

				runner.SkipAction   = skipAction != null ? () => skipAction(expression, ps) : null as Func<int>;
				runner.TakeAction   = takeAction != null ? () => takeAction(expression, ps) : null as Func<int>;
				runner.QueryContext = queryContext;
				runner.DataContext  = dataContext;

				try
				{
					mapper.QueryRunner = runner;

					var dr = await runner.ExecuteReaderAsync(cancellationToken, options);
					await dr.QueryForEachAsync(m, r =>
					{
						var b = func(r);
						runner.RowsCount++;
						return b;
					}, cancellationToken);
				}
				finally
				{
					mapper.QueryRunner = null;
				}
			}
		}

#endif

		static void SetRunQuery<T>(
			Query<T> query,
			Expression<Func<IQueryRunner,IDataReader,T>> expression)
		{
			var executeQuery = GetExecuteQuery<T>(query, ExecuteQuery);

			ClearParameters(query);

			var mapper   = new Mapper<T>(expression);
			var runQuery = executeQuery.Item1;

			query.GetIEnumerable = (ctx,db,expr,ps) => runQuery(query, ctx, db, mapper, expr, ps, 0);

#if !NOASYNC

			var skipAction = executeQuery.Item2;
			var takeAction = executeQuery.Item3;

			query.GetForEachAsync = (ctx, db, expr, ps, action, token, options) =>
				ExecuteQueryAsync(query, ctx, db, mapper, expr, ps, 0, action, skipAction, takeAction, token, options);

#endif
		}

		static readonly PropertyInfo _queryContextInfo = MemberHelper.PropertyOf<IQueryRunner>( p => p.QueryContext);
		static readonly PropertyInfo _dataContextInfo  = MemberHelper.PropertyOf<IQueryRunner>( p => p.DataContext);
		static readonly PropertyInfo _expressionInfo   = MemberHelper.PropertyOf<IQueryRunner>( p => p.Expression);
		static readonly PropertyInfo _parametersnfo    = MemberHelper.PropertyOf<IQueryRunner>( p => p.Parameters);
		static readonly PropertyInfo _rowsCountnfo     = MemberHelper.PropertyOf<IQueryRunner>( p => p.RowsCount);

		static Expression<Func<IQueryRunner,IDataReader,T>> WrapMapper<T>(Expression<Func<QueryContext,IDataContext,IDataReader,Expression,object[],T>> expression)
		{
			var queryRunnerParam = Expression.Parameter(typeof(IQueryRunner), "qr");
			var dataReaderParam = Expression.Parameter(typeof(IDataReader), "dr");

			return
				Expression.Lambda<Func<IQueryRunner,IDataReader,T>>(
					Expression.Invoke(
						expression, new[]
						{
							Expression.Property(queryRunnerParam, _queryContextInfo) as Expression,
							Expression.Property(queryRunnerParam, _dataContextInfo),
							dataReaderParam,
							Expression.Property(queryRunnerParam, _expressionInfo),
							Expression.Property(queryRunnerParam, _parametersnfo),
						}),
					queryRunnerParam,
					dataReaderParam);
		}

		#endregion

		#region SetRunQuery / Cast, Concat, Union, OfType, ScalarSelect, Select, SequenceContext, Table

		public static void SetRunQuery<T>(
			Query<T> query,
			Expression<Func<QueryContext,IDataContext,IDataReader,Expression,object[],T>> expression)
		{
			var l = WrapMapper(expression);

			SetRunQuery(query, l);
		}

		#endregion

		#region SetRunQuery / Select 2

		public static void SetRunQuery<T>(
			Query<T> query,
			Expression<Func<QueryContext,IDataContext,IDataReader,Expression,object[],int,T>> expression)
		{
			var queryRunnerParam = Expression.Parameter(typeof(IQueryRunner), "qr");
			var dataReaderParam  = Expression.Parameter(typeof(IDataReader),  "dr");

			var l =
				Expression.Lambda<Func<IQueryRunner,IDataReader,T>>(
					Expression.Invoke(
						expression, new[]
						{
							Expression.Property(queryRunnerParam, _queryContextInfo) as Expression,
							Expression.Property(queryRunnerParam, _dataContextInfo),
							dataReaderParam,
							Expression.Property(queryRunnerParam, _expressionInfo),
							Expression.Property(queryRunnerParam, _parametersnfo),
							Expression.Property(queryRunnerParam, _rowsCountnfo),
						}),
					queryRunnerParam,
					dataReaderParam);

			SetRunQuery(query, l);
		}

		#endregion

		#region SetRunQuery / Aggregation, All, Any, Contains, Count

		public static void SetRunQuery<T>(
			Query<T> query,
			Expression<Func<QueryContext,IDataContext,IDataReader,Expression,object[],object>> expression)
		{
			FinalizeQuery(query);

			if (query.Queries.Count != 1)
				throw new InvalidOperationException();

			ClearParameters(query);

			var l      = WrapMapper(expression);
			var mapper = new Mapper<object>(l);

			query.GetElement = (ctx, db, expr, ps) => ExecuteElement(query, ctx, db, mapper, expr, ps);

#if !NOASYNC
			query.GetElementAsync = (ctx, db, expr, ps, token, options) =>
				ExecuteElementAsync<object>(query, ctx, db, mapper, expr, ps, token, options);
#endif
		}

		static T ExecuteElement<T>(
			Query          query,
			QueryContext   queryContext,
			IDataContextEx dataContext,
			Mapper<T> mapper,
			Expression     expression,
			object[]       ps)
		{
			if (queryContext == null)
				queryContext = new QueryContext(dataContext, expression, ps);

			using (var runner = dataContext.GetQueryRunner(query, 0, expression, ps))
			{
				runner.QueryContext = queryContext;
				runner.DataContext  = dataContext;

				try
				{
					mapper.QueryRunner = runner;

					using (var dr = runner.ExecuteReader())
					{
						while (dr.Read())
						{
							var value = mapper.Map(runner, dr);
							runner.RowsCount++;
							return value;
						}
					}

					return Array<T>.Empty.First();
				}
				finally
				{
					mapper.QueryRunner = null;
				}
			}
		}

#if !NOASYNC

		static async Task<T> ExecuteElementAsync<T>(
			Query                         query,
			QueryContext                  queryContext,
			IDataContextEx                dataContext,
			Mapper<object>                mapper,
			Expression                    expression,
			object[]                      ps,
			CancellationToken             cancellationToken,
			TaskCreationOptions           options)
		{
			if (queryContext == null)
				queryContext = new QueryContext(dataContext, expression, ps);

			using (var runner = dataContext.GetQueryRunner(query, 0, expression, ps))
			{
				Func<IDataReader,object> m = dr => mapper.Map(runner, dr);

				runner.QueryContext = queryContext;
				runner.DataContext  = dataContext;

				try
				{
					mapper.QueryRunner = runner;

					var dr = await runner.ExecuteReaderAsync(cancellationToken, options);

					var item = default(T);
					var read = false;

					await dr.QueryForEachAsync(
						m,
						r =>
						{
							read = true;
							item = dataContext.MappingSchema.ChangeTypeTo<T>(r);
							runner.RowsCount++;
							return false;
						},
						cancellationToken);

					if (read)
						return item;

					return Array<T>.Empty.First();
				}
				finally
				{
					mapper.QueryRunner = null;
				}
			}
		}

#endif

		#endregion

		#region ScalarQuery

		public static void SetScalarQuery<T>(Query<T> query)
		{
			FinalizeQuery(query);

			if (query.Queries.Count != 1)
				throw new InvalidOperationException();

			ClearParameters(query);

			query.GetElement = (ctx, db, expr, ps) => ScalarQuery(query, db, expr, ps);
		}

		static object ScalarQuery<T>(Query<T> query, IDataContextEx dataContext, Expression expr, object[] parameters)
		{
			using (var runner = dataContext.GetQueryRunner(query, 0, expr, parameters))
			{
				runner.QueryContext = new QueryContext(dataContext, expr, parameters);
				runner.DataContext  = dataContext;

				return runner.ExecuteScalar();
			}
		}

		#endregion
	}
}