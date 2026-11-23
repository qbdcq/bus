--
-- PostgreSQL database dump
--

-- Dumped from database version 18.4
-- Dumped by pg_dump version 18.4

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: card; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.card (
    card_no text NOT NULL,
    balance numeric(10,2) DEFAULT 0 NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    property character varying,
    CONSTRAINT card_balance_check CHECK ((balance >= (0)::numeric))
);


--
-- Name: fare_rule; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.fare_rule (
    rule_id integer NOT NULL,
    base_fare numeric(6,2) NOT NULL,
    base_distance_m integer NOT NULL,
    step_distance_m integer NOT NULL,
    step_fare numeric(6,2) NOT NULL,
    max_fare numeric(6,2),
    effective_from date DEFAULT CURRENT_DATE NOT NULL,
    effective_to date,
    discount_normal numeric(3,2) DEFAULT 1.00 NOT NULL,
    discount_student numeric(3,2) DEFAULT 1.00 NOT NULL,
    discount_elder numeric(3,2) DEFAULT 1.00 NOT NULL,
    discount_love numeric(3,2) DEFAULT 1.00 NOT NULL,
    CONSTRAINT fare_rule_discount_range_check CHECK (((discount_normal >= (0)::numeric) AND (discount_normal <= (1)::numeric) AND (discount_student >= (0)::numeric) AND (discount_student <= (1)::numeric) AND (discount_elder >= (0)::numeric) AND (discount_elder <= (1)::numeric) AND (discount_love >= (0)::numeric) AND (discount_love <= (1)::numeric))),
    CONSTRAINT fare_rule_step_distance_m_check CHECK ((step_distance_m > 0))
);


--
-- Name: fare_rule_rule_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.fare_rule_rule_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: fare_rule_rule_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.fare_rule_rule_id_seq OWNED BY public.fare_rule.rule_id;


--
-- Name: station; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.station (
    seq integer NOT NULL,
    name text NOT NULL
);


--
-- Name: station_distance; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.station_distance (
    seq_from integer NOT NULL,
    seq_to integer NOT NULL,
    distance_m integer NOT NULL,
    CONSTRAINT station_distance_check CHECK ((seq_to = (seq_from + 1))),
    CONSTRAINT station_distance_distance_m_check CHECK ((distance_m > 0))
);


--
-- Name: trip; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.trip (
    trip_id bigint NOT NULL,
    card_no text NOT NULL,
    board_seq integer NOT NULL,
    alight_seq integer,
    board_time timestamp with time zone DEFAULT now() NOT NULL,
    alight_time timestamp with time zone,
    distance_m integer,
    fare numeric(6,2),
    rule_id integer,
    pre_deducted numeric(10,2) DEFAULT 0 NOT NULL,
    status text DEFAULT 'completed'::text NOT NULL,
    CONSTRAINT trip_check CHECK ((board_seq <> alight_seq)),
    CONSTRAINT trip_status_check CHECK ((status = ANY (ARRAY['open'::text, 'completed'::text, 'missing_alight'::text])))
);


--
-- Name: trip_trip_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.trip_trip_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: trip_trip_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.trip_trip_id_seq OWNED BY public.trip.trip_id;


--
-- Name: fare_rule rule_id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fare_rule ALTER COLUMN rule_id SET DEFAULT nextval('public.fare_rule_rule_id_seq'::regclass);


--
-- Name: trip trip_id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trip ALTER COLUMN trip_id SET DEFAULT nextval('public.trip_trip_id_seq'::regclass);


--
-- Data for Name: card; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.card (card_no, balance, updated_at, property) FROM stdin;
CARD001	50.00	2026-11-18 20:34:48.306992+08	\N
CARD002	30.00	2026-11-18 20:34:48.306992+08	\N
CARD003	20.00	2026-11-18 20:34:48.306992+08	\N
CARD004	80.00	2026-11-18 20:34:48.306992+08	\N
CARD005	15.00	2026-11-18 20:34:48.306992+08	\N
1234567899	100.00	2026-11-18 21:30:54.439292+08	\N
0123456789	50.00	2026-11-18 21:30:54.439292+08	\N
0005860000	30.00	2026-11-18 21:30:54.439292+08	\N
1111111111	20.00	2026-11-18 21:30:54.439292+08	\N
0006849456	15.50	2026-11-18 21:30:54.439292+08	\N
0006852948	97.00	2026-11-18 21:30:54.439292+08	\N
0006081976	48.50	2026-11-18 21:30:54.439292+08	\N
0006624725	7.40	2026-11-23 08:14:12.711422+08	\N
0009521992	35.74	2026-11-23 09:00:23.856243+08	学生卡
0009568222	9.55	2026-11-23 09:19:09.681571+08	敬老卡
\.


--
-- Data for Name: fare_rule; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.fare_rule (rule_id, base_fare, base_distance_m, step_distance_m, step_fare, max_fare, effective_from, effective_to, discount_normal, discount_student, discount_elder, discount_love) FROM stdin;
1	2.00	10000	5000	1.00	\N	2026-01-01	\N	1.00	0.50	0.50	0.00
\.


--
-- Data for Name: station; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.station (seq, name) FROM stdin;
1	天洋城总站
2	天洋城
3	燕郊富地广场
4	紫竹园小区
5	燕郊中美医院
6	华北科技学院北门
7	中赵甫村
8	防灾科技学院
9	交通干部管理学院
10	潮白人家小区
11	欧逸丽庭
12	通州白庙村
13	白庙新村
14	北京胸科医院
15	北京物资学院南站
16	大北窑南
\.


--
-- Data for Name: station_distance; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.station_distance (seq_from, seq_to, distance_m) FROM stdin;
2	3	658
3	4	673
4	5	603
5	6	592
6	7	515
7	8	699
8	9	255
9	10	853
10	11	635
11	12	1500
12	13	940
13	14	9900
14	15	1000
15	16	16000
1	2	1500
\.


--
-- Data for Name: trip; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.trip (trip_id, card_no, board_seq, alight_seq, board_time, alight_time, distance_m, fare, rule_id, pre_deducted, status) FROM stdin;
9	CARD001	1	16	2026-11-18 07:15:00+08	2026-11-18 07:40:00+08	36323	8.00	1	0.00	completed
10	CARD002	3	10	2026-11-18 08:05:00+08	2026-11-18 08:30:00+08	4190	2.00	1	0.00	completed
11	CARD003	1	6	2026-11-18 08:40:00+08	2026-11-18 09:05:00+08	4026	2.00	1	0.00	completed
12	CARD004	10	16	2026-11-18 17:20:00+08	2026-11-18 17:45:00+08	29975	6.00	1	0.00	completed
13	CARD005	5	15	2026-11-18 18:10:00+08	2026-11-18 18:35:00+08	16889	4.00	1	0.00	completed
27	0009521992	2	5	2026-11-23 08:20:10.104721+08	2026-11-23 08:20:22.020201+08	1934	2.00	\N	7.26	completed
28	0009521992	9	11	2026-11-23 08:23:12.398594+08	2026-11-23 08:23:21.528891+08	1488	1.00	\N	3.63	completed
29	0009521992	11	13	2026-11-23 08:23:55.948735+08	2026-11-23 08:23:58.881803+08	2440	1.00	\N	3.63	completed
30	0009521992	13	14	2026-11-23 08:24:00.644465+08	2026-11-23 08:24:01.675427+08	9900	1.00	\N	3.63	completed
31	0009521992	1	11	2026-11-23 08:24:29.350157+08	2026-11-23 08:24:34.523215+08	6983	1.00	\N	3.63	completed
32	0009521992	12	13	2026-11-23 08:24:56.949347+08	2026-11-23 08:25:00.01061+08	940	1.00	\N	3.63	completed
33	0009568222	2	6	2026-11-23 08:25:35.850111+08	2026-11-23 08:25:41.772733+08	2526	1.00	\N	3.63	completed
25	0006624725	1	16	2026-11-23 08:14:12.710822+08	2026-11-23 08:43:02.77107+08	36323	7.26	\N	7.26	missing_alight
36	0009568222	10	\N	2026-11-23 08:43:51.826987+08	\N	\N	3.63	\N	3.63	missing_alight
37	0009568222	10	16	2026-11-23 08:51:13.400327+08	2026-11-23 08:51:17.902822+08	29975	3.63	\N	3.63	missing_alight
38	0009568222	1	16	2026-11-23 08:51:46.383173+08	2026-11-23 08:51:48.999453+08	36323	3.63	\N	3.63	missing_alight
39	0009568222	4	16	2026-11-23 08:52:03.142839+08	2026-11-23 08:52:10.495105+08	33492	3.63	\N	3.63	missing_alight
40	0009521992	14	16	2026-11-23 08:52:45.930152+08	2026-11-23 08:52:50.466865+08	17000	3.63	\N	3.63	missing_alight
41	0009568222	6	16	2026-11-23 09:00:16.866809+08	2026-11-23 09:00:32.308237+08	32297	3.63	\N	3.63	missing_alight
42	0009521992	6	16	2026-11-23 09:00:23.855483+08	2026-11-23 09:00:32.308237+08	32297	3.63	\N	3.63	missing_alight
43	0009568222	14	10	2026-11-23 09:19:04.929654+08	2026-11-23 09:19:09.679094+08	12975	1.30	\N	3.63	completed
\.


--
-- Name: fare_rule_rule_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('public.fare_rule_rule_id_seq', 1, true);


--
-- Name: trip_trip_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('public.trip_trip_id_seq', 43, true);


--
-- Name: card card_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.card
    ADD CONSTRAINT card_pkey PRIMARY KEY (card_no);


--
-- Name: fare_rule fare_rule_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fare_rule
    ADD CONSTRAINT fare_rule_pkey PRIMARY KEY (rule_id);


--
-- Name: station_distance station_distance_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.station_distance
    ADD CONSTRAINT station_distance_pkey PRIMARY KEY (seq_from, seq_to);


--
-- Name: station station_name_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.station
    ADD CONSTRAINT station_name_key UNIQUE (name);


--
-- Name: station station_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.station
    ADD CONSTRAINT station_pkey PRIMARY KEY (seq);


--
-- Name: trip trip_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trip
    ADD CONSTRAINT trip_pkey PRIMARY KEY (trip_id);


--
-- Name: ix_trip_board_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_trip_board_time ON public.trip USING btree (board_time);


--
-- Name: ix_trip_card_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_trip_card_status ON public.trip USING btree (card_no, status);


--
-- Name: ix_trip_card_time; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_trip_card_time ON public.trip USING btree (card_no, board_time DESC);


--
-- Name: ux_trip_open; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_trip_open ON public.trip USING btree (card_no) WHERE (status = 'open'::text);


--
-- Name: station_distance station_distance_seq_from_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.station_distance
    ADD CONSTRAINT station_distance_seq_from_fkey FOREIGN KEY (seq_from) REFERENCES public.station(seq);


--
-- Name: station_distance station_distance_seq_to_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.station_distance
    ADD CONSTRAINT station_distance_seq_to_fkey FOREIGN KEY (seq_to) REFERENCES public.station(seq);


--
-- Name: trip trip_alight_seq_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trip
    ADD CONSTRAINT trip_alight_seq_fkey FOREIGN KEY (alight_seq) REFERENCES public.station(seq);


--
-- Name: trip trip_board_seq_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trip
    ADD CONSTRAINT trip_board_seq_fkey FOREIGN KEY (board_seq) REFERENCES public.station(seq);


--
-- Name: trip trip_card_no_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.trip
    ADD CONSTRAINT trip_card_no_fkey FOREIGN KEY (card_no) REFERENCES public.card(card_no);


--
-- PostgreSQL database dump complete